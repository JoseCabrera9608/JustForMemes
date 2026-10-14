using UnityEngine;

namespace FixMyCity.Roads
{
    /// <summary>
    /// Una vía de 2 carriles definida por una polilínea de waypoints.
    ///
    /// IDEA CLAVE (por qué no usamos física ni NavMesh):
    /// Un bus del Metropolitano nunca "sale" de su vía. Así que en vez de mover el bus libremente
    /// en 3D, lo describimos con DOS números:
    ///
    ///   - distance : cuántos metros lleva recorridos a lo largo del eje de la vía (1D).
    ///   - lateral  : cuánto está desplazado a izquierda/derecha del eje (el carril).
    ///
    /// Posición en el mundo = PuntoDelEje(distance) + derecha * lateral.
    ///
    /// Ventajas para celular:
    ///   - Cero Rigidbody, cero raycasts, cero colliders.
    ///   - "¿Quién va delante de mí?" se vuelve comparar dos floats (distance), no lanzar rayos.
    ///   - Es determinista: el mismo input da el mismo resultado (bueno para depurar y para balancear).
    ///
    /// Los waypoints se "hornean" (Bake) en arrays al iniciar: después, evaluar una posición es
    /// O(1) gracias a un índice de segmento que cada bus recuerda (segmentHint).
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class RoadPath : MonoBehaviour
    {
        [Header("Geometría")]
        [Tooltip("Waypoints en orden de marcha. Si se deja vacío, se usan los hijos directos en orden de jerarquía.")]
        [SerializeField] private Transform[] waypoints;

        [Header("Carriles (offset lateral desde el eje; + = derecha en sentido de marcha)")]
        [Tooltip("Carril de puertas/paradas. Negativo = a la izquierda del eje.")]
        [SerializeField] private float leftLaneOffset = -3.4f;
        [Tooltip("Carril expreso.")]
        [SerializeField] private float rightLaneOffset = 3.4f;

        [Header("Límite de velocidad por carril (m/s)  —  10 m/s = 36 km/h")]
        [Tooltip("Carril de puertas: más lento, simula prudencia junto a los paraderos.")]
        [SerializeField, Min(0.5f)] private float leftLaneSpeedLimit = 10f;
        [Tooltip("Carril expreso: aquí el bus puede ir a su velocidad máxima.")]
        [SerializeField, Min(0.5f)] private float rightLaneSpeedLimit = 15f;

        // ---- Datos horneados (solo lectura en runtime) ----
        private Vector3[] _points;      // posiciones mundo de cada waypoint
        private float[] _cumulative;    // distancia acumulada hasta cada waypoint
        private Vector3[] _forwards;    // dirección normalizada de cada segmento
        private Vector3[] _rights;      // perpendicular derecha de cada segmento (plano XZ)
        private float _length;
        private bool _baked;

        public float Length { get { EnsureBaked(); return _length; } }
        public float LaneWidth => Mathf.Abs(rightLaneOffset - leftLaneOffset);
        public float LeftLaneOffset => leftLaneOffset;
        public float RightLaneOffset => rightLaneOffset;

        public float GetLaneOffset(BusLane lane) => lane == BusLane.Left ? leftLaneOffset : rightLaneOffset;

        public float GetLaneSpeedLimit(BusLane lane) => lane == BusLane.Left ? leftLaneSpeedLimit : rightLaneSpeedLimit;

        /// <summary>
        /// Límite de velocidad para un offset lateral cualquiera. Mientras el bus cambia de carril
        /// mezclamos ambos límites, así la aceleración/frenado al cambiar de carril es progresiva.
        /// </summary>
        public float GetSpeedLimitAt(float lateral)
        {
            float t = Mathf.InverseLerp(leftLaneOffset, rightLaneOffset, lateral);
            return Mathf.Lerp(leftLaneSpeedLimit, rightLaneSpeedLimit, t);
        }

        /// <summary>Carril cuyo centro está más cerca de este offset lateral.</summary>
        public BusLane GetClosestLane(float lateral)
        {
            return Mathf.Abs(lateral - leftLaneOffset) <= Mathf.Abs(lateral - rightLaneOffset)
                ? BusLane.Left
                : BusLane.Right;
        }

        private void Awake() => Bake();

        private void OnValidate() => _baked = false;

        private void EnsureBaked()
        {
            if (!_baked) Bake();
        }

        /// <summary>
        /// Convierte los Transforms en arrays planos. Se llama una vez en Awake (la vía es estática).
        /// En el editor se re-hornea al dibujar gizmos para que veas los cambios al mover waypoints.
        /// </summary>
        public void Bake()
        {
            int count = CountWaypoints();
            if (count < 2)
            {
                Debug.LogError($"[RoadPath] '{name}' necesita al menos 2 waypoints.", this);
                _points = new[] { transform.position, transform.position + transform.forward };
                count = 2;
            }
            else
            {
                if (_points == null || _points.Length != count) _points = new Vector3[count];
                for (int i = 0; i < count; i++) _points[i] = GetWaypoint(i).position;
            }

            int segments = count - 1;
            if (_cumulative == null || _cumulative.Length != count) _cumulative = new float[count];
            if (_forwards == null || _forwards.Length != segments) _forwards = new Vector3[segments];
            if (_rights == null || _rights.Length != segments) _rights = new Vector3[segments];

            _cumulative[0] = 0f;
            for (int i = 0; i < segments; i++)
            {
                Vector3 delta = _points[i + 1] - _points[i];
                float segLength = delta.magnitude;
                _cumulative[i + 1] = _cumulative[i] + segLength;

                Vector3 fwd = segLength > 0.0001f ? delta / segLength : Vector3.forward;
                _forwards[i] = fwd;

                // Derecha = up x forward, aplanado en XZ (la vía puede tener algo de pendiente,
                // pero el carril siempre se desplaza en horizontal).
                Vector3 right = Vector3.Cross(Vector3.up, fwd);
                right.y = 0f;
                _rights[i] = right.sqrMagnitude > 0.0001f ? right.normalized : Vector3.right;
            }

            _length = _cumulative[count - 1];
            _baked = true;
        }

        /// <summary>
        /// Devuelve el punto del EJE de la vía a 'distance' metros del inicio, junto con su dirección.
        /// 'segmentHint' es el último segmento usado por ese bus: como los buses avanzan poco por frame,
        /// casi siempre siguen en el mismo segmento → la búsqueda es O(1) en vez de recorrer todos.
        /// </summary>
        public void Evaluate(float distance, ref int segmentHint, out Vector3 position, out Vector3 forward, out Vector3 right)
        {
            EnsureBaked();
            int lastSegment = _forwards.Length - 1;
            distance = Mathf.Clamp(distance, 0f, _length);
            segmentHint = Mathf.Clamp(segmentHint, 0, lastSegment);

            while (segmentHint < lastSegment && distance > _cumulative[segmentHint + 1]) segmentHint++;
            while (segmentHint > 0 && distance < _cumulative[segmentHint]) segmentHint--;

            float along = distance - _cumulative[segmentHint];
            forward = _forwards[segmentHint];
            right = _rights[segmentHint];
            position = _points[segmentHint] + forward * along;
        }

        /// <summary>Posición en el mundo para (distance, lateral). Atajo cuando no hay hint disponible.</summary>
        public Vector3 GetWorldPosition(float distance, float lateral)
        {
            int hint = 0;
            Evaluate(distance, ref hint, out Vector3 pos, out _, out Vector3 right);
            return pos + right * lateral;
        }

        /// <summary>
        /// Proyecta un punto del mundo sobre el eje y devuelve su 'distance'.
        /// Recorre todos los segmentos (O(n)), así que SOLO se usa en inicialización
        /// (ej.: calcular dónde cae cada paradero sobre la vía), nunca por frame.
        /// </summary>
        public float Project(Vector3 worldPoint)
        {
            EnsureBaked();
            float bestDistance = 0f;
            float bestSqr = float.MaxValue;

            for (int i = 0; i < _forwards.Length; i++)
            {
                Vector3 a = _points[i];
                float segLength = _cumulative[i + 1] - _cumulative[i];
                Vector3 toPoint = worldPoint - a;
                toPoint.y = 0f;
                Vector3 fwdFlat = _forwards[i];
                fwdFlat.y = 0f;

                float along = Mathf.Clamp(Vector3.Dot(toPoint, fwdFlat.normalized), 0f, segLength);
                Vector3 closest = a + _forwards[i] * along;
                Vector3 diff = worldPoint - closest;
                diff.y = 0f;
                float sqr = diff.sqrMagnitude;

                if (sqr < bestSqr)
                {
                    bestSqr = sqr;
                    bestDistance = _cumulative[i] + along;
                }
            }

            return bestDistance;
        }

        private int CountWaypoints()
        {
            if (waypoints != null && waypoints.Length > 0)
            {
                int valid = 0;
                for (int i = 0; i < waypoints.Length; i++) if (waypoints[i] != null) valid++;
                return valid;
            }
            return transform.childCount;
        }

        private Transform GetWaypoint(int index)
        {
            if (waypoints != null && waypoints.Length > 0)
            {
                int valid = 0;
                for (int i = 0; i < waypoints.Length; i++)
                {
                    if (waypoints[i] == null) continue;
                    if (valid == index) return waypoints[i];
                    valid++;
                }
            }
            return transform.GetChild(index);
        }

#if UNITY_EDITOR
        // Colores pedidos en el spec: carril de puertas rojo (lento), carril expreso verde (rápido).
        private static readonly Color AxisColor = new Color(1f, 1f, 1f, 0.35f);
        private static readonly Color LeftLaneColor = new Color(1f, 0.25f, 0.25f, 0.9f);
        private static readonly Color RightLaneColor = new Color(0.25f, 1f, 0.35f, 0.9f);

        private void OnDrawGizmos()
        {
            if (!Application.isPlaying) Bake();
            if (_points == null || _points.Length < 2) return;

            for (int i = 0; i < _forwards.Length; i++)
            {
                Vector3 a = _points[i];
                Vector3 b = _points[i + 1];
                Vector3 r = _rights[i];

                Gizmos.color = AxisColor;
                Gizmos.DrawLine(a, b);

                Gizmos.color = LeftLaneColor;
                Gizmos.DrawLine(a + r * leftLaneOffset, b + r * leftLaneOffset);

                Gizmos.color = RightLaneColor;
                Gizmos.DrawLine(a + r * rightLaneOffset, b + r * rightLaneOffset);

                // Flecha de sentido de marcha a mitad de segmento.
                Vector3 mid = (a + b) * 0.5f;
                Vector3 fwd = _forwards[i];
                Gizmos.color = Color.white;
                Gizmos.DrawLine(mid, mid - fwd * 2f + r * 1f);
                Gizmos.DrawLine(mid, mid - fwd * 2f - r * 1f);
            }

            Gizmos.color = Color.white;
            for (int i = 0; i < _points.Length; i++) Gizmos.DrawWireSphere(_points[i], 0.6f);
        }
#endif
    }
}
