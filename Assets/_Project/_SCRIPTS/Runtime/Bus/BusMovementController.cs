using System;
using System.Collections.Generic;
using FixMyCity.Roads;
using FixMyCity.Routes;
using FixMyCity.Stations;
using FixMyCity.Traffic;
using UnityEngine;

namespace FixMyCity.Buses
{
    /// <summary>
    /// Controla el movimiento de UN bus. Va en el GameObject raíz del prefab del bus.
    ///
    /// NO tiene Update(): el BusTrafficManager lo "tickea" en dos fases (Sense → Integrate).
    ///
    /// Movimiento = dos ejes independientes:
    ///   LONGITUDINAL (avanzar/frenar): modelo IDM (Intelligent Driver Model), el estándar de los
    ///       simuladores de tráfico. Una sola fórmula da: acelerar suave, mantener distancia con el de
    ///       adelante y frenar progresivamente hasta el punto exacto del paradero.
    ///   LATERAL (cambiar de carril): el offset lateral persigue el centro del carril objetivo,
    ///       pero su velocidad está limitada por la velocidad de avance (ángulo de giro máximo).
    ///       → Un bus detenido no puede "deslizarse" de lado; al arrancar sale del carril en diagonal.
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class BusMovementController : MonoBehaviour
    {
        [SerializeField] private BusStats stats;

        [Tooltip("Puntos de embarque (Puerta1, Puerta2). Si se deja vacío se buscan por nombre. " +
                 "Los usará el sistema de pasajeros.")]
        [SerializeField] private Transform[] doors;

        // Velocidad por debajo de la cual se permite "encajar" exacto en el paradero.
        private const float ArriveSpeed = 1.5f;
        // Ganancia del controlador lateral: más alto = entra al carril más decidido.
        private const float LateralGain = 2.5f;
        // Para calcular la velocidad lateral usamos al menos esta velocidad de avance (m/s): permite
        // terminar de acomodarse en el carril a paso de hombre (maniobra de aproximación al andén).
        private const float MinManeuverSpeed = 2f;
        // Error lateral máximo (m) para dar por "pegado al andén" y abrir puertas.
        private const float DockLateralTolerance = 1f;
        // Le cedo el paso a un bus que espera meterse a mi carril si va a menos de esta velocidad (m/s).
        private const float YieldToMergerMaxSpeed = 3f;

        private readonly BusMovementData _data = new BusMovementData();
        private BusStats _defaultStats;
        private BusTrafficManager _traffic;
        private BusRoute _route;

        // Calculado en Sense, aplicado en Integrate.
        private float _pendingAcceleration;

        // Bus que estoy siguiendo (para debug visual y futura UI).
        private BusMovementController _leader;
        // Bus del carril de puertas detrás del cual intento meterme (cuando aún no hay hueco).
        private BusMovementController _mergeTarget;

        // ------------------------------------------------------------------ Eventos
        // Ganchos para los sistemas que vendrán (pasajeros, satisfacción, UI). El bus no sabe quién escucha.

        /// <summary>El bus se detuvo y abrió puertas en un paradero.</summary>
        public event Action<BusMovementController, BusStop> BoardingStarted;
        /// <summary>Cerró puertas y vuelve a circular.</summary>
        public event Action<BusMovementController, BusStop> BoardingCompleted;
        /// <summary>No pudo meterse al carril de puertas a tiempo y se pasó el paradero (futuro: penalización).</summary>
        public event Action<BusMovementController, BusStop> StopMissed;
        /// <summary>Llegó al final de la vía.</summary>
        public event Action<BusMovementController> RouteCompleted;

        // ------------------------------------------------------------------ Lectura pública

        public BusStats Stats => stats;
        /// <summary>Stats con los que viene el prefab (para restaurarlos al reciclar el bus).</summary>
        public BusStats DefaultStats => _defaultStats != null ? _defaultStats : stats;
        public BusMovementData Data => _data;
        public BusRoute Route => _route;
        public RoadPath Road => _route?.Road;
        public IReadOnlyList<Transform> Doors => doors;

        public float Distance => _data.Distance;
        public float Speed => _data.Speed;
        public float FrontDistance => _data.Distance + stats.HalfLength;
        public float RearDistance => _data.Distance - stats.HalfLength;

        /// <summary>Bus de adelante que condiciona mi velocidad (null si vía libre).</summary>
        public BusMovementController CurrentLeader => _leader;

        public bool IsBoarding => _data.State == BusState.Boarding;
        public bool IsActiveOnRoad => _route != null && (_data.State == BusState.Driving || _data.State == BusState.Boarding);

        public BusStop NextStop =>
            _route != null && _data.NextStopIndex < _route.StopCount ? _route.GetStop(_data.NextStopIndex) : null;

        /// <summary>Índice en la lista ordenada del manager. Lo escribe el manager cada paso.</summary>
        public int SortedIndex { get; internal set; }

        // ------------------------------------------------------------------ Ciclo de vida

        private void Awake()
        {
            if (doors == null || doors.Length == 0)
            {
                Transform d1 = transform.Find("Puerta1");
                Transform d2 = transform.Find("Puerta2");
                if (d1 != null && d2 != null) doors = new[] { d1, d2 };
            }
            _defaultStats = stats;
            _data.Reset();
        }

        private void OnDisable()
        {
            // Red de seguridad: si alguien desactiva el bus sin pasar por el pool, que no quede "fantasma" en el tráfico.
            if (_traffic != null) _traffic.Unregister(this);
        }

        /// <summary>
        /// Pone al bus a circular en una ruta. Lo llama el RouteManager tras sacarlo del pool.
        /// </summary>
        public void AssignRoute(BusRoute route, BusTrafficManager traffic, float startDistance, BusLane startLane, float startSpeed = 0f)
        {
            if (stats == null)
            {
                Debug.LogError($"[Bus] '{name}' no tiene BusStats asignado.", this);
                return;
            }

            if (_traffic != null) _traffic.Unregister(this);

            _route = route;
            _traffic = traffic;
            _data.Reset();

            RoadPath road = route.Road;
            _data.State = BusState.Driving;
            _data.Distance = startDistance;
            _data.Speed = Mathf.Max(0f, startSpeed);
            _data.LateralOffset = road.GetLaneOffset(startLane);
            _data.CurrentLane = startLane;
            _data.TargetLane = startLane;
            // Desfasamos el temporizador de decisión entre buses para repartir el trabajo entre frames.
            _data.LaneDecisionTimer = UnityEngine.Random.Range(0f, traffic.Rules.laneDecisionInterval);

            // Saltar paraderos que ya quedaron atrás del punto de aparición.
            while (NextStop != null && NextStop.DistanceOnRoad < startDistance - stats.arriveTolerance)
                _data.NextStopIndex++;

            _traffic.Register(this);
            ApplyTransform(0f);
        }

        /// <summary>Saca al bus del tráfico (antes de volver al pool).</summary>
        public void ReleaseRoute()
        {
            if (_traffic != null) _traffic.Unregister(this);
            _traffic = null;
            _route = null;
            _leader = null;
            _mergeTarget = null;
            _data.Reset();
        }

        public void SetStats(BusStats newStats) => stats = newStats;

        // ================================================================== FASE 1: SENSE
        // Solo LEE el estado de otros buses. No escribe posiciones.

        internal void Sense(List<BusMovementController> sorted, TrafficRules rules, float dt, float time)
        {
            _pendingAcceleration = 0f;
            if (_route == null || _data.State != BusState.Driving) return;

            RoadPath road = _route.Road;

            // 1) Decisión de carril (no cada frame: cada laneDecisionInterval segundos).
            _data.LaneDecisionTimer -= dt;
            if (_data.LaneDecisionTimer <= 0f)
            {
                _data.LaneDecisionTimer += rules.laneDecisionInterval;
                if (_data.LaneDecisionTimer <= 0f) _data.LaneDecisionTimer = rules.laneDecisionInterval;
                UpdateLaneDecision(sorted, road, rules, time);
            }

            // 2) Aceleración longitudinal (IDM).
            _pendingAcceleration = ComputeAcceleration(sorted, road, rules);
        }

        private void UpdateLaneDecision(List<BusMovementController> sorted, RoadPath road, TrafficRules rules, float time)
        {
            BusStop stop = NextStop;
            NeighborInfo leftLeader = BusDetectionSystem.FindLeaderInLane(
                sorted, SortedIndex, BusLane.Left, road, rules, rules.detectionRange);

            var input = new LaneDecisionInput
            {
                IsBoarding = false,
                HasNextStop = stop != null,
                DistanceToNextStop = stop != null ? stop.DistanceOnRoad - _data.Distance : float.PositiveInfinity,
                NextStopDistanceOnRoad = stop != null ? stop.DistanceOnRoad : 0f,
                LaneReturnDistance = rules.laneReturnDistance,
                PreferExpressLaneWhenFar = rules.preferExpressLaneWhenFar,
                LeftLeaderExists = leftLeader.Exists,
                LeftLeaderIsStopped = leftLeader.Exists &&
                                      (leftLeader.Bus.IsBoarding || leftLeader.Bus.Speed < rules.stoppedSpeedThreshold),
                LeftLeaderFrontDistance = leftLeader.Exists ? leftLeader.Bus.FrontDistance : 0f,
                LeftLeaderRearDistance = leftLeader.Exists ? leftLeader.Bus.RearDistance : 0f,
                MyFrontDistance = FrontDistance,
                // Desde el frente del bus detenido hasta MI paradero (donde va mi centro) necesito:
                // mi media longitud trasera + hueco mínimo + metros para cruzar de carril
                // + metros para frenar (v²/2b) + margen.
                RequiredMergeSpace = stats.HalfLength + stats.minimumGap +
                                     stats.LaneChangeDistance(road.LaneWidth, _data.Speed) +
                                     _data.Speed * _data.Speed / (2f * stats.comfortableDeceleration) +
                                     rules.mergeMargin
            };

            BusLane desired = BusLaneSystem.DecideLane(in input, out bool overtaking, out bool needsDoorsLane);

            // ¿Necesito el carril de puertas para MI paradero? (lo usa el cálculo de velocidad)
            _data.WantsDoorsLane = needsDoorsLane;
            _mergeTarget = desired == BusLane.Left ? leftLeader.Bus : null;

            _data.IsOvertaking = overtaking && _data.TargetLane == BusLane.Right;
            if (desired == _data.TargetLane) return;

            // Anti-zigzag: entre cambios voluntarios hay un tiempo mínimo.
            // Volver al carril de puertas para MI paradero es obligatorio → no espera.
            bool mandatory = needsDoorsLane;
            if (!mandatory && time - _data.LastLaneChangeTime < rules.minTimeBetweenLaneChanges) return;

            // "Querer" no es "poder": solo cambiamos si hay hueco en el carril destino.
            // Si no hay, lo reintentamos en la próxima decisión (0.2 s después).
            if (!BusDetectionSystem.IsLaneChangeSafe(sorted, SortedIndex, desired, road, rules)) return;

            _data.TargetLane = desired;
            _data.LastLaneChangeTime = time;
            _data.IsOvertaking = overtaking && desired == BusLane.Right;
        }

        /// <summary>
        /// IDM: aceleración = término de vía libre − término de interacción.
        ///
        ///   vía libre    = a · (1 − (v/v0)^4)           → acelera fuerte desde parado, suave al llegar a v0.
        ///   interacción  = a · (s* / s)^2                → crece cuando el hueco real 's' es menor al deseado 's*'.
        ///   s* = s0 + v·T + v·Δv / (2·√(a·b))            → hueco deseado: mínimo + tiempo de reacción + "me acerco rápido".
        ///
        /// El paradero se trata como un "bus invisible detenido" justo en la línea de parada:
        /// así el mismo modelo frena suave y exacto, sin código especial de frenado.
        /// </summary>
        private float ComputeAcceleration(List<BusMovementController> sorted, RoadPath road, TrafficRules rules)
        {
            float v = _data.Speed;
            float v0 = Mathf.Min(stats.maxSpeed, road.GetSpeedLimitAt(_data.LateralOffset));

            BusStop stop = NextStop;
            float distToStop = stop != null ? stop.DistanceOnRoad - _data.Distance : float.PositiveInfinity;

            // Si mi paradero está cerca, no corro aunque siga en el carril expreso: tengo que poder meterme y frenar.
            // Empiezo a bajar a la velocidad del carril de puertas ANTES de la zona de retorno (lo que tarde en
            // frenar de mi velocidad máxima a la del carril de puertas), para no tener que frenar en seco.
            float doorsLaneSpeed = road.GetLaneSpeedLimit(BusLane.Left);
            float slowDownDistance = rules.laneReturnDistance +
                Mathf.Max(0f, stats.maxSpeed * stats.maxSpeed - doorsLaneSpeed * doorsLaneSpeed) / (2f * stats.comfortableDeceleration);
            if (distToStop <= slowDownDistance)
                v0 = Mathf.Min(v0, doorsLaneSpeed);

            float a = stats.maxAcceleration;
            float b = stats.comfortableDeceleration;

            // Término de vía libre. Limitado a frenado cómodo: si paso a un carril más lento no "clavo" el freno.
            float ratio = v / Mathf.Max(v0, 0.1f);
            float ratio2 = ratio * ratio;
            float freeRoad = Mathf.Max(a * (1f - ratio2 * ratio2), -b);

            // Interacción con el bus de adelante (en cualquiera de los carriles que ocupo).
            float interaction = 0f;
            NeighborInfo leader = BusDetectionSystem.FindLeaderInOccupiedLanes(sorted, SortedIndex, road, rules, rules.detectionRange);
            _leader = leader.Bus;
            if (leader.Exists)
            {
                interaction = InteractionTerm(v, leader.Gap, v - leader.Bus.Speed, stats.minimumGap, stats.timeHeadway, a, b);
            }

            // Cortesía: si voy por el carril de puertas y adelante hay un bus casi detenido esperando para
            // meterse a él (a su paradero), le cedo el paso.
            if (BusDetectionSystem.OccupiesLane(this, BusLane.Left, road, rules))
            {
                NeighborInfo merger = BusDetectionSystem.FindMergerAhead(sorted, SortedIndex, road, rules,
                                                                         rules.detectionRange, YieldToMergerMaxSpeed);
                if (merger.Exists)
                {
                    float yieldTerm = InteractionTerm(v, merger.Gap, v - merger.Bus.Speed, stats.minimumGap, stats.timeHeadway, a, b);
                    if (yieldTerm > interaction) interaction = yieldTerm;
                }
            }

            // Interacción con el paradero: si voy (o necesito ir) al carril de puertas, freno para él
            // aunque todavía esté cruzando de carril. Así nunca llego "pasado" de velocidad.
            if (stop != null && (_data.TargetLane == BusLane.Left || _data.WantsDoorsLane))
            {
                float stopTerm = InteractionTerm(v, distToStop, v, 0f, stats.stopTimeHeadway, a, b);
                if (stopTerm > interaction) interaction = stopTerm; // nos quedamos con el más restrictivo
            }

            // Quiero meterme al carril de puertas pero aún no hay hueco: freno como si el bus de ese
            // carril estuviera delante de mí, para acabar DETRÁS de él y poder colarme (como un chofer real).
            if (_data.WantsDoorsLane && _data.TargetLane == BusLane.Right &&
                _mergeTarget != null && _mergeTarget.IsActiveOnRoad)
            {
                float mergeGap = BusDetectionSystem.GapBetween(this, _mergeTarget);
                // Solo si puedo quedar detrás frenando de forma cómoda. Si ya es tarde, NO clavo el freno:
                // sigo por la derecha hasta mi paradero y allí los de la izquierda me ceden el paso.
                float closing = v - _mergeTarget.Speed;
                float room = mergeGap - stats.minimumGap;
                bool comfortable = closing <= 0f || (room > 0f && closing * closing / (2f * room) <= b);
                if (mergeGap > 0f && comfortable)
                {
                    float mergeTerm = InteractionTerm(v, mergeGap, v - _mergeTarget.Speed, stats.minimumGap, stats.timeHeadway, a, b);
                    // Tope: esta maniobra es opcional, nunca debe pedir más que un frenado cómodo.
                    mergeTerm = Mathf.Min(mergeTerm, freeRoad + b);
                    if (mergeTerm > interaction) interaction = mergeTerm;
                }
            }

            float acc = freeRoad - interaction;
            return Mathf.Clamp(acc, -stats.emergencyDeceleration, stats.maxAcceleration);
        }

        private static float InteractionTerm(float v, float gap, float approachingRate, float s0, float timeHeadway, float a, float b)
        {
            float desiredGap = s0 + Mathf.Max(0f, v * timeHeadway + v * approachingRate / (2f * Mathf.Sqrt(a * b)));
            float s = Mathf.Max(gap, 0.01f);
            float r = desiredGap / s;
            return a * r * r;
        }

        // ================================================================== FASE 2: INTEGRATE
        // Solo ESCRIBE el estado propio.

        internal void Integrate(float dt)
        {
            if (_route == null) return;
            RoadPath road = _route.Road;

            switch (_data.State)
            {
                case BusState.Driving:
                    IntegrateLongitudinal(dt);
                    break;
                case BusState.Boarding:
                    UpdateBoarding(dt);
                    break;
                default:
                    return;
            }

            IntegrateLateral(dt, road);
            ApplyTransform(dt);

            if (_data.State == BusState.Driving) CheckStopsAndRouteEnd(road);
        }

        private void IntegrateLongitudinal(float dt)
        {
            // Euler semi-implícito: primero velocidad, luego posición con la velocidad nueva (más estable).
            _data.Acceleration = _pendingAcceleration;
            _data.Speed = Mathf.Max(0f, _data.Speed + _pendingAcceleration * dt);
            _data.Distance += _data.Speed * dt;
        }

        private void IntegrateLateral(float dt, RoadPath road)
        {
            float target = road.GetLaneOffset(_data.TargetLane);
            float error = target - _data.LateralOffset;

            // Velocidad lateral máxima: la menor entre
            //   - la nominal (cruzar el carril en laneChangeDuration), y
            //   - la permitida por el giro: v_lateral = v_avance · tan(ángulo_máx).
            float nominal = road.LaneWidth / stats.laneChangeDuration;
            float bySteering = Mathf.Max(_data.Speed, MinManeuverSpeed) * Mathf.Tan(stats.maxSteerAngle * Mathf.Deg2Rad);
            float maxLateral = Mathf.Min(nominal, bySteering);

            float desiredVelocity = Mathf.Clamp(error * LateralGain, -maxLateral, maxLateral);
            float lateralAccel = nominal * 2.5f;
            _data.LateralVelocity = Mathf.MoveTowards(_data.LateralVelocity, desiredVelocity, lateralAccel * dt);
            _data.LateralOffset += _data.LateralVelocity * dt;

            if (Mathf.Abs(error) < 0.01f && Mathf.Abs(_data.LateralVelocity) < 0.05f)
            {
                _data.LateralOffset = target;
                _data.LateralVelocity = 0f;
            }

            _data.CurrentLane = road.GetClosestLane(_data.LateralOffset);
        }

        private void CheckStopsAndRouteEnd(RoadPath road)
        {
            BusStop stop = NextStop;
            if (stop != null)
            {
                float d = stop.DistanceOnRoad - _data.Distance;
                float tol = stats.arriveTolerance;

                bool docked = Mathf.Abs(_data.LateralOffset - road.LeftLaneOffset) < DockLateralTolerance;
                if (d <= tol && d >= -tol * 2f && _data.Speed <= ArriveSpeed && _data.TargetLane == BusLane.Left && docked)
                {
                    BeginBoarding(stop);
                    return;
                }

                if (d < -tol * 2f)
                {
                    // Se lo pasó (no pudo meterse a tiempo). Seguimos al siguiente paradero.
                    _data.NextStopIndex++;
                    StopMissed?.Invoke(this, stop);
                }
            }

            if (FrontDistance >= road.Length)
            {
                _data.State = BusState.Finished;
                _data.Speed = 0f;
                RouteCompleted?.Invoke(this);
            }
        }

        private void BeginBoarding(BusStop stop)
        {
            // Encaje exacto: el transform raíz queda alineado con el paradero (requisito del diseño).
            _data.Distance = stop.DistanceOnRoad;
            _data.Speed = 0f;
            _data.Acceleration = 0f;
            _data.State = BusState.Boarding;
            _data.TargetLane = BusLane.Left;
            _data.IsOvertaking = false;
            _data.WantsDoorsLane = false;
            _data.BoardingTimer = 0f;
            _data.BoardingProgress = 0f;
            _data.BoardingDuration = stats.boardingTime * _route.BoardingTimeMultiplier;

            BoardingStarted?.Invoke(this, stop);
        }

        private void UpdateBoarding(float dt)
        {
            _data.BoardingTimer += dt;
            _data.BoardingProgress = _data.BoardingDuration > 0f
                ? Mathf.Clamp01(_data.BoardingTimer / _data.BoardingDuration)
                : 1f;

            if (_data.BoardingTimer < _data.BoardingDuration) return;

            BusStop stop = NextStop;
            _data.NextStopIndex++;
            _data.State = BusState.Driving;

            // Al cerrar puertas re-evaluamos el carril YA (sin esperar al temporizador ni al anti-zigzag):
            // es el momento exacto del Video 2 → "¿el de adelante sigue embarcando? entonces lo paso".
            _data.LaneDecisionTimer = 0f;
            _data.LastLaneChangeTime = -999f;

            BoardingCompleted?.Invoke(this, stop);
        }

        private void ApplyTransform(float dt)
        {
            RoadPath road = _route.Road;
            road.Evaluate(_data.Distance, ref _data.SegmentHint, out Vector3 axisPos, out Vector3 forward, out Vector3 right);

            Vector3 position = axisPos + right * _data.LateralOffset;

            // El morro apunta en la dirección real de movimiento (avance + lateral), así el cambio de
            // carril se VE como un giro y no como un bus deslizándose de lado.
            // Max(Speed, 3) evita giros exagerados a muy baja velocidad.
            float targetYaw = Mathf.Atan2(_data.LateralVelocity, Mathf.Max(_data.Speed, 3f)) * Mathf.Rad2Deg;
            _data.VisualYaw = dt > 0f
                ? Mathf.Lerp(_data.VisualYaw, targetYaw, 1f - Mathf.Exp(-8f * dt))
                : targetYaw;

            Quaternion rotation = Quaternion.LookRotation(forward, Vector3.up) * Quaternion.Euler(0f, _data.VisualYaw, 0f);
            transform.SetPositionAndRotation(position, rotation);
        }

#if UNITY_EDITOR
        // ------------------------------------------------------------------ Debug visual
        private static readonly Color BoardingColor = new Color(1f, 0.85f, 0.1f);
        private static readonly Color OvertakingColor = new Color(1f, 0.2f, 1f);
        private static readonly Color ExpressColor = new Color(0.25f, 1f, 0.35f);
        private static readonly Color DoorsLaneColor = new Color(1f, 0.35f, 0.25f);

        private void OnDrawGizmos()
        {
            if (stats == null) return;

            Color c = !Application.isPlaying || _route == null ? Color.gray
                : _data.State == BusState.Boarding ? BoardingColor
                : _data.IsOvertaking ? OvertakingColor
                : _data.TargetLane == BusLane.Right ? ExpressColor
                : DoorsLaneColor;

            // Caja del bus (huella usada para distancias de seguridad).
            Gizmos.color = c;
            Matrix4x4 old = Gizmos.matrix;
            Gizmos.matrix = transform.localToWorldMatrix;
            Gizmos.DrawWireCube(new Vector3(0f, 1.6f, 0f), new Vector3(2.6f, 3.2f, stats.length));
            Gizmos.matrix = old;

            if (!Application.isPlaying || _route == null) return;

            // Línea al bus que estoy siguiendo.
            if (_leader != null)
            {
                Gizmos.color = Color.cyan;
                Gizmos.DrawLine(transform.position + Vector3.up * 3.5f, _leader.transform.position + Vector3.up * 3.5f);
            }

            string state = _data.State == BusState.Boarding ? $"EMBARCANDO {_data.BoardingProgress:P0}"
                : _data.IsOvertaking ? "ADELANTANDO"
                : _data.TargetLane == BusLane.Right ? "EXPRESO" : "PUERTAS";
            string next = NextStop != null ? NextStop.StopId : "-";
            UnityEditor.Handles.color = c;
            UnityEditor.Handles.Label(transform.position + Vector3.up * 4.5f,
                $"{_route.RouteName}  {_data.Speed * 3.6f:0} km/h\n{state} → {next}");
        }
#endif
    }
}
