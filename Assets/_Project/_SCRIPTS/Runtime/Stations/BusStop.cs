using FixMyCity.Roads;
using UnityEngine;

namespace FixMyCity.Stations
{
    /// <summary>
    /// Un paradero (ej.: "ParaderoNorte1"). Va en el GameObject del paradero.
    ///
    /// REGLA DE ALINEACIÓN: el bus se detiene cuando su transform raíz queda alineado (en el eje de la
    /// vía, en esta escena = eje Z) con el transform de este paradero. Para lograrlo proyectamos la
    /// posición del paradero sobre la vía UNA sola vez y guardamos esa distancia (DistanceOnRoad).
    /// Luego el bus solo compara su 'distance' con este número.
    ///
    /// Si mueves el paradero en el editor, el gizmo amarillo te muestra dónde quedará el bus.
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class BusStop : MonoBehaviour
    {
        [Tooltip("Vía sobre la que está este paradero.")]
        [SerializeField] private RoadPath road;

        [Tooltip("Id corto para UI y debug. Ej.: JP-N1")]
        [SerializeField] private string stopId;

        private float _distanceOnRoad;
        private bool _resolved;

        public RoadPath Road => road;
        public string StopId => string.IsNullOrEmpty(stopId) ? name : stopId;
        public Station Station { get; private set; }

        /// <summary>Metros desde el inicio de la vía donde el bus debe detenerse.</summary>
        public float DistanceOnRoad
        {
            get
            {
                if (!_resolved) Resolve();
                return _distanceOnRoad;
            }
        }

        private void Awake()
        {
            Station = GetComponentInParent<Station>();
            Resolve();
        }

        private void OnValidate() => _resolved = false;

        /// <summary>Recalcula la proyección del paradero sobre la vía (llamar si se mueve en runtime).</summary>
        public void Resolve()
        {
            if (road == null)
            {
                Debug.LogError($"[BusStop] '{name}' no tiene RoadPath asignado.", this);
                _distanceOnRoad = 0f;
                return;
            }

            _distanceOnRoad = road.Project(transform.position);
            _resolved = true;
        }

        public void Configure(RoadPath newRoad, string newStopId)
        {
            road = newRoad;
            stopId = newStopId;
            _resolved = false;
        }

#if UNITY_EDITOR
        private void OnDrawGizmos()
        {
            if (road == null) return;
            if (!Application.isPlaying) _resolved = false;

            float d = DistanceOnRoad;
            int hint = 0;
            road.Evaluate(d, ref hint, out Vector3 axis, out Vector3 fwd, out Vector3 right);

            // Línea de parada sobre el carril de puertas: aquí se alinea el transform raíz del bus.
            Vector3 laneCenter = axis + right * road.LeftLaneOffset;
            Vector3 half = right * (road.LaneWidth * 0.5f);
            Gizmos.color = Color.yellow;
            Gizmos.DrawLine(laneCenter - half, laneCenter + half);
            Gizmos.DrawLine(transform.position, laneCenter);
            Gizmos.DrawWireSphere(laneCenter, 0.4f);

            UnityEditor.Handles.color = Color.yellow;
            UnityEditor.Handles.Label(laneCenter + Vector3.up * 2f, $"{StopId}\n{d:0.0} m");
        }
#endif
    }
}
