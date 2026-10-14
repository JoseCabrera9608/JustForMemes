using FixMyCity.Roads;

namespace FixMyCity.Buses
{
    /// <summary>Estado de alto nivel del bus.</summary>
    public enum BusState : byte
    {
        /// <summary>En el pool, sin ruta.</summary>
        Inactive,
        /// <summary>Circulando (incluye aproximarse a un paradero y frenar).</summary>
        Driving,
        /// <summary>Detenido en el paradero con puertas abiertas.</summary>
        Boarding,
        /// <summary>Llegó al final de la vía / ruta.</summary>
        Finished
    }

    /// <summary>
    /// Estado RUNTIME de un bus (lo que cambia cada frame).
    ///
    /// Separación de responsabilidades:
    ///   BusStats           = "qué bus es"        (asset compartido, no cambia)
    ///   BusMovementData    = "dónde está y qué hace" (por bus, cambia cada frame)
    ///   BusMovementController = "cómo se mueve"  (lógica)
    ///
    /// Es una clase plana (sin MonoBehaviour) para poder resetearla al reciclar el bus del pool sin
    /// generar basura (GC), y para poder leerla desde UI/sistemas sin depender del GameObject.
    /// Los campos son públicos a propósito: es un contenedor de datos, la lógica vive en el controller.
    /// </summary>
    public sealed class BusMovementData
    {
        // --- Longitudinal (a lo largo de la vía) ---
        /// <summary>Metros recorridos desde el inicio de la vía (posición del transform raíz).</summary>
        public float Distance;
        /// <summary>Velocidad de avance (m/s). Nunca negativa: los buses no retroceden.</summary>
        public float Speed;
        /// <summary>Aceleración calculada este frame (m/s²). Útil para debug y futuras luces de freno.</summary>
        public float Acceleration;

        // --- Lateral (carriles) ---
        /// <summary>Desplazamiento lateral actual respecto al eje de la vía (m).</summary>
        public float LateralOffset;
        /// <summary>Velocidad lateral actual (m/s). Se usa también para inclinar el morro del bus al cambiar de carril.</summary>
        public float LateralVelocity;
        /// <summary>Carril en el que está físicamente (el más cercano a LateralOffset).</summary>
        public BusLane CurrentLane;
        /// <summary>Carril al que quiere ir.</summary>
        public BusLane TargetLane;
        /// <summary>True si está en el carril expreso específicamente para pasar a un bus detenido.</summary>
        public bool IsOvertaking;
        /// <summary>
        /// True si necesita volver al carril de puertas para su paradero (aunque todavía no haya hueco).
        /// Mientras sea true el bus va frenando para poder "colarse" detrás del bus del carril de puertas.
        /// </summary>
        public bool WantsDoorsLane;

        // --- Ruta ---
        public BusState State;
        /// <summary>Índice (en la ruta) del próximo paradero. == StopCount cuando ya no quedan.</summary>
        public int NextStopIndex;
        /// <summary>Segundos que lleva en el paradero actual.</summary>
        public float BoardingTimer;
        /// <summary>Duración total del embarque actual (s).</summary>
        public float BoardingDuration;
        /// <summary>0..1 progreso del embarque (para UI: barra sobre el bus).</summary>
        public float BoardingProgress;

        // --- Internos / cachés ---
        /// <summary>Último segmento de la vía usado (hace la evaluación O(1)).</summary>
        public int SegmentHint;
        /// <summary>Tiempo restante para la próxima decisión de carril (no decidimos cada frame).</summary>
        public float LaneDecisionTimer;
        /// <summary>Tiempo (Time.time) del último cambio de carril, para evitar zigzag.</summary>
        public float LastLaneChangeTime;
        /// <summary>Ángulo visual (°) del morro respecto a la vía, suavizado.</summary>
        public float VisualYaw;

        public void Reset()
        {
            Distance = 0f;
            Speed = 0f;
            Acceleration = 0f;
            LateralOffset = 0f;
            LateralVelocity = 0f;
            CurrentLane = BusLane.Left;
            TargetLane = BusLane.Left;
            IsOvertaking = false;
            WantsDoorsLane = false;
            State = BusState.Inactive;
            NextStopIndex = 0;
            BoardingTimer = 0f;
            BoardingDuration = 0f;
            BoardingProgress = 0f;
            SegmentHint = 0;
            LaneDecisionTimer = 0f;
            LastLaneChangeTime = -999f;
            VisualYaw = 0f;
        }
    }
}
