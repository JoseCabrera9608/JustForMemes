using UnityEngine;

namespace FixMyCity.Buses
{
    /// <summary>
    /// Estadísticas de un MODELO de bus (datos que no cambian durante el viaje).
    ///
    /// Es un ScriptableObject porque:
    ///   - Muchos buses comparten el mismo modelo → un solo asset en memoria, no 50 copias.
    ///   - Las mejoras de flota (más capacidad, más velocidad) serán simplemente otro asset
    ///     (BusStats_Mejorado) que asignas al bus.
    ///   - El diseñador balancea en el inspector sin tocar código, incluso en Play Mode.
    ///
    /// Las unidades son del SI: metros, segundos, m/s, m/s². (15 m/s ≈ 54 km/h)
    /// </summary>
    [CreateAssetMenu(menuName = "Fix My City/Bus Stats", fileName = "BusStats_Default")]
    public sealed class BusStats : ScriptableObject
    {
        [Header("Capacidad")]
        [Min(1)] public int passengerCapacity = 15;

        [Header("Dimensiones (para distancias de seguridad)")]
        [Tooltip("Largo total del bus. El transform raíz se asume en el centro.")]
        [Min(1f)] public float length = 11.5f;

        [Header("Velocidad y aceleración")]
        [Tooltip("Velocidad máxima propia del bus (m/s). El carril puede limitarla más.")]
        [Min(0.5f)] public float maxSpeed = 15f;
        [Tooltip("Aceleración máxima (m/s²). Un bus real lleno anda por 1–2.")]
        [Min(0.1f)] public float maxAcceleration = 2f;
        [Tooltip("Frenado cómodo (m/s²). Es el que usa normalmente para parar en el paradero.")]
        [Min(0.1f)] public float comfortableDeceleration = 2f;
        [Tooltip("Frenado máximo de emergencia (m/s²). Tope físico: si no alcanza, puede haber choque.")]
        [Min(0.1f)] public float emergencyDeceleration = 6f;

        [Header("Seguimiento del bus de adelante (modelo IDM)")]
        [Tooltip("Distancia mínima (m) que deja con el bus de adelante estando detenido.")]
        [Min(0f)] public float minimumGap = 2.5f;
        [Tooltip("Separación en TIEMPO con el de adelante (s). 1.2 s = conducción prudente.")]
        [Min(0.1f)] public float timeHeadway = 1.2f;
        [Tooltip("Separación en tiempo al aproximarse al paradero. Más bajo = frena más tarde y más preciso.")]
        [Min(0.05f)] public float stopTimeHeadway = 0.4f;

        [Header("Paradas")]
        [Tooltip("Segundos que espera en cada paradero antes de cerrar puertas (luego lo controlará el sistema de pasajeros).")]
        [Min(0f)] public float boardingTime = 4f;
        [Tooltip("Tolerancia (m) para dar por llegado al paradero y alinear exactamente.")]
        [Min(0.05f)] public float arriveTolerance = 0.4f;

        [Header("Cambio de carril")]
        [Tooltip("Tiempo aproximado para cruzar de un carril al otro a velocidad normal (s).")]
        [Min(0.2f)] public float laneChangeDuration = 1.8f;
        [Tooltip("Ángulo máximo de giro (°). Limita la velocidad lateral según la velocidad de avance: " +
                 "un bus detenido NO puede moverse de lado; uno lento cambia de carril más despacio.")]
        [Range(3f, 35f)] public float maxSteerAngle = 25f;

        public float HalfLength => length * 0.5f;

        /// <summary>
        /// Metros de avance que necesita para cruzar un carril de 'laneWidth' metros yendo a 'speed'.
        /// Es el mayor de dos límites:
        ///   - geométrico: con el giro máximo, por cada metro lateral avanza 1/tan(ángulo) metros.
        ///   - temporal:   a velocidad alta, tarda laneChangeDuration segundos → recorre speed·duración.
        /// Lo usa la lógica de adelantamiento para saber si "cabe" adelantar y volver a meterse.
        /// </summary>
        public float LaneChangeDistance(float laneWidth, float speed)
        {
            float geometric = laneWidth / Mathf.Tan(maxSteerAngle * Mathf.Deg2Rad);
            float temporal = speed * laneChangeDuration;
            return Mathf.Max(geometric, temporal);
        }
    }
}
