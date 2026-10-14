using System;
using System.Collections.Generic;
using FixMyCity.Buses;
using FixMyCity.Pooling;
using FixMyCity.Roads;
using FixMyCity.Traffic;
using UnityEngine;

namespace FixMyCity.Routes
{
    /// <summary>
    /// Administra las rutas y la salida de buses (despacho). En el juego final el jugador creará rutas
    /// y asignará buses de su flota; por ahora cada ruta despacha buses cada X segundos para probar el tráfico.
    ///
    /// Dependencias explícitas (inspector), sin singletons:
    ///   RouteManager ──usa──► BusPool          (de dónde saco buses)
    ///                └─usa──► BusTrafficManager (quién los simula)
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class RouteManager : MonoBehaviour
    {
        /// <summary>Una ruta + su configuración de despacho.</summary>
        [Serializable]
        public sealed class RouteDispatch
        {
            public BusRoute route = new BusRoute();
            [Tooltip("Segundos entre salidas de buses de esta ruta (frecuencia).")]
            [Min(1f)] public float dispatchInterval = 15f;
            [Tooltip("Retraso antes del primer bus (para no sacar todas las rutas a la vez).")]
            [Min(0f)] public float firstDispatchDelay;
            [Tooltip("Máximo de buses de esta ruta circulando a la vez (tamaño de su flota).")]
            [Min(1)] public int maxActiveBuses = 3;
            [Tooltip("Stats del modelo de bus para esta ruta. Si es null se usa el del prefab.")]
            public BusStats busStatsOverride;

            [NonSerialized] public float Timer;
            [NonSerialized] public int ActiveCount;
            [NonSerialized] public bool Valid;
        }

        [SerializeField] private BusPool pool;
        [SerializeField] private BusTrafficManager traffic;

        [Tooltip("Metros desde el inicio de la vía donde aparecen los buses.")]
        [SerializeField, Min(0f)] private float spawnDistance = 8f;
        [Tooltip("Velocidad (m/s) con la que entran a la vía (vienen del patio, no de parado).")]
        [SerializeField, Min(0f)] private float spawnSpeed = 6f;

        [SerializeField] private List<RouteDispatch> routes = new List<RouteDispatch>();

        [Header("Debug")]
        [Tooltip("Escribe en consola cada vez que un bus empieza a embarcar.")]
        [SerializeField] private bool logBoarding;

        // Qué ruta lleva cada bus activo (para descontar al terminar). Diccionario preasignado.
        private readonly Dictionary<BusMovementController, RouteDispatch> _busToRoute =
            new Dictionary<BusMovementController, RouteDispatch>(32);

        // Delegados cacheados: suscribir con un método directo crearía un delegado nuevo (basura) cada vez.
        private Action<BusMovementController> _onRouteCompleted;
        private Action<BusMovementController, Stations.BusStop> _onStopMissed;
        private Action<BusMovementController, Stations.BusStop> _onBoardingStarted;

        public IReadOnlyList<RouteDispatch> Routes => routes;

        private void Awake()
        {
            _onRouteCompleted = HandleRouteCompleted;
            _onStopMissed = HandleStopMissed;
            _onBoardingStarted = HandleBoardingStarted;
        }

        private void Start()
        {
            if (pool == null || traffic == null)
            {
                Debug.LogError("[RouteManager] Asigna BusPool y BusTrafficManager en el inspector.", this);
                enabled = false;
                return;
            }

            for (int i = 0; i < routes.Count; i++)
            {
                RouteDispatch r = routes[i];
                r.Valid = r.route.Initialize(out string error);
                if (!r.Valid) Debug.LogError($"[RouteManager] {error}", this);
                r.Timer = r.firstDispatchDelay;
                r.ActiveCount = 0;
            }
        }

        private void Update()
        {
            float dt = Time.deltaTime;
            for (int i = 0; i < routes.Count; i++)
            {
                RouteDispatch r = routes[i];
                if (!r.Valid) continue;

                r.Timer -= dt;
                if (r.Timer > 0f || r.ActiveCount >= r.maxActiveBuses) continue;

                // Si la entrada está ocupada, reintentamos el próximo frame (el timer queda en ≤ 0).
                if (TryDispatch(r)) r.Timer = r.dispatchInterval;
            }
        }

        private bool TryDispatch(RouteDispatch dispatch)
        {
            BusRoute route = dispatch.route;
            RoadPath road = route.Road;

            BusLane lane = ChooseSpawnLane(route);
            float halfLength = EstimateHalfLength(dispatch);
            float distance = Mathf.Max(spawnDistance, halfLength + 0.5f);

            if (!traffic.IsSpaceFree(road, distance, halfLength, lane, clearance: 10f)) return false;

            BusMovementController bus = pool.Get();
            if (bus == null) return false;

            // Siempre asignamos stats: un bus reciclado pudo venir de otra ruta con otro modelo.
            bus.SetStats(dispatch.busStatsOverride != null ? dispatch.busStatsOverride : bus.DefaultStats);

            bus.RouteCompleted += _onRouteCompleted;
            bus.StopMissed += _onStopMissed;
            bus.BoardingStarted += _onBoardingStarted;

            bus.AssignRoute(route, traffic, distance, lane, spawnSpeed);
            _busToRoute[bus] = dispatch;
            dispatch.ActiveCount++;
            return true;
        }

        /// <summary>Si el primer paradero está lejos sale por el expreso, si no por el de puertas.</summary>
        private BusLane ChooseSpawnLane(BusRoute route)
        {
            TrafficRules rules = traffic.Rules;
            if (route.StopCount == 0) return BusLane.Right;
            float firstStop = route.GetStop(0).DistanceOnRoad;
            bool far = firstStop - spawnDistance > rules.laneReturnDistance;
            return far && rules.preferExpressLaneWhenFar ? BusLane.Right : BusLane.Left;
        }

        private float EstimateHalfLength(RouteDispatch dispatch)
        {
            if (dispatch.busStatsOverride != null) return dispatch.busStatsOverride.HalfLength;
            BusStats prefabStats = pool.PrefabStats;
            return prefabStats != null ? prefabStats.HalfLength : 6f;
        }

        private void HandleRouteCompleted(BusMovementController bus)
        {
            if (_busToRoute.TryGetValue(bus, out RouteDispatch dispatch))
            {
                dispatch.ActiveCount = Mathf.Max(0, dispatch.ActiveCount - 1);
                _busToRoute.Remove(bus);
            }

            bus.RouteCompleted -= _onRouteCompleted;
            bus.StopMissed -= _onStopMissed;
            bus.BoardingStarted -= _onBoardingStarted;

            // PENDIENTE (fase de ida/vuelta): aquí el bus haría la vuelta en U en la estación central.
            // Por ahora vuelve al pool y el despacho sacará otro.
            pool.Release(bus);
        }

        private void HandleStopMissed(BusMovementController bus, Stations.BusStop stop)
        {
            Debug.LogWarning($"[Ruta {bus.Route.RouteName}] {bus.name} se pasó el paradero {stop.StopId} (no pudo meterse al carril de puertas).", bus);
        }

        private void HandleBoardingStarted(BusMovementController bus, Stations.BusStop stop)
        {
            if (logBoarding) Debug.Log($"[Ruta {bus.Route.RouteName}] {bus.name} embarcando en {stop.StopId}", bus);
        }

        // ------------------------------------------------------------------ API de construcción

        public void Configure(BusPool busPool, BusTrafficManager trafficManager)
        {
            pool = busPool;
            traffic = trafficManager;
        }

        public void ClearRoutes() => routes.Clear();

        public void AddRoute(BusRoute route, float dispatchInterval, float firstDelay, int maxActive)
        {
            routes.Add(new RouteDispatch
            {
                route = route,
                dispatchInterval = dispatchInterval,
                firstDispatchDelay = firstDelay,
                maxActiveBuses = maxActive
            });
        }
    }
}
