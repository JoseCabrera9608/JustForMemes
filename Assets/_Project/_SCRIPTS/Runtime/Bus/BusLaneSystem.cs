using FixMyCity.Roads;

namespace FixMyCity.Buses
{
    /// <summary>
    /// Todo lo que la decisión de carril necesita saber, empaquetado en un struct.
    /// El controller lo rellena a partir del mundo; BusLaneSystem solo lee números.
    /// </summary>
    public struct LaneDecisionInput
    {
        public bool IsBoarding;

        public bool HasNextStop;
        /// <summary>Metros desde el bus hasta su próximo paradero.</summary>
        public float DistanceToNextStop;
        /// <summary>Posición absoluta (en la vía) del próximo paradero.</summary>
        public float NextStopDistanceOnRoad;

        public float LaneReturnDistance;
        public bool PreferExpressLaneWhenFar;

        /// <summary>¿Hay un bus adelante en el carril de puertas (dentro del rango de detección)?</summary>
        public bool LeftLeaderExists;
        /// <summary>¿Ese bus está detenido / embarcando?</summary>
        public bool LeftLeaderIsStopped;
        /// <summary>Posición absoluta del parachoques DELANTERO de ese bus.</summary>
        public float LeftLeaderFrontDistance;
        /// <summary>Posición absoluta del parachoques TRASERO de ese bus (el final de la cola).</summary>
        public float LeftLeaderRearDistance;
        /// <summary>Posición absoluta de MI parachoques delantero.</summary>
        public float MyFrontDistance;

        /// <summary>
        /// Espacio mínimo que necesito entre el frente del bus detenido y mi paradero para adelantarlo
        /// y volver a meterme: mi largo + distancia de cambio de carril + margen.
        /// </summary>
        public float RequiredMergeSpace;
    }

    /// <summary>
    /// CEREBRO DE CARRILES. Decide a qué carril QUIERE ir el bus.
    ///
    /// Es una clase estática con funciones puras (entrada → salida, sin estado, sin Unity):
    ///   - Se puede probar con tests unitarios sin abrir una escena.
    ///   - Toda la "personalidad" del tráfico está en un solo lugar fácil de leer y cambiar.
    ///   - No sabe nada de seguridad: decide el DESEO. Si el carril está libre lo comprueba
    ///     BusDetectionSystem.IsLaneChangeSafe (separar "querer" de "poder").
    ///
    /// Reglas (sacadas del análisis de los videos + doc de Mecánicas):
    ///   R1. Embarcando → carril de puertas (Left), bloqueado.
    ///   R2. Próximo paradero LEJOS (> laneReturnDistance) o ninguno → carril expreso (Right).
    ///       [Video 1: el SX y los buses que no paran aquí van por la derecha]
    ///   R3. Próximo paradero CERCA → carril de puertas (Left)...
    ///   R4. ...EXCEPTO si delante, en el carril de puertas, hay un bus DETENIDO antes de mi paradero
    ///       y hay espacio para pasarlo y volver a meterme → adelanto por la derecha.
    ///       [Video 2: termina de embarcar, ve al de adelante aún embarcando, lo pasa por la derecha]
    ///       Si NO hay espacio → me quedo en la cola detrás de él (como en la vida real).
    ///   R5. Si hay una COLA de buses detenidos antes de mi paradero que no puedo adelantar, la distancia
    ///       de retorno se mide hasta el FINAL DE LA COLA (no hasta el paradero). Así me cambio a tiempo
    ///       para ponerme detrás, en vez de llegar por la derecha y tener que colarme.
    /// </summary>
    public static class BusLaneSystem
    {
        /// <param name="isOvertaking">True si elige el expreso para pasar a un bus detenido (para el debug visual).</param>
        /// <param name="needsDoorsLane">True si DEBE volver al carril de puertas para su paradero (prioridad, sin anti-zigzag).</param>
        public static BusLane DecideLane(in LaneDecisionInput input, out bool isOvertaking, out bool needsDoorsLane)
        {
            isOvertaking = false;
            needsDoorsLane = false;

            // R1
            if (input.IsBoarding) return BusLane.Left;

            bool blockedByStoppedBus = input.LeftLeaderExists && input.LeftLeaderIsStopped;
            bool canOvertake = blockedByStoppedBus && HasRoomToOvertake(in input);

            // R5: distancia "efectiva" hasta donde tengo que estar en el carril de puertas.
            float effectiveDistance = input.DistanceToNextStop;
            bool queueBeforeMyStop = blockedByStoppedBus && !canOvertake && input.HasNextStop &&
                                     input.LeftLeaderRearDistance < input.NextStopDistanceOnRoad;
            if (queueBeforeMyStop)
            {
                float toQueueEnd = input.LeftLeaderRearDistance - input.MyFrontDistance;
                if (toQueueEnd < effectiveDistance) effectiveDistance = toQueueEnd;
            }

            bool nearStop = input.HasNextStop && effectiveDistance <= input.LaneReturnDistance;
            needsDoorsLane = nearStop && !canOvertake;

            if (!nearStop)
            {
                // R2: lejos del próximo paradero.
                if (input.PreferExpressLaneWhenFar)
                {
                    isOvertaking = blockedByStoppedBus; // ya iba por la derecha; solo marcamos para el debug visual
                    return BusLane.Right;
                }

                // Variante de diseño (preferExpressLaneWhenFar = false): sigue por la izquierda
                // y solo sale a la derecha si alguien detenido le bloquea.
                if (canOvertake)
                {
                    isOvertaking = true;
                    return BusLane.Right;
                }
                return BusLane.Left;
            }

            // R4
            if (canOvertake)
            {
                isOvertaking = true;
                return BusLane.Right;
            }

            // R3
            return BusLane.Left;
        }

        /// <summary>¿Cabe adelantar al bus detenido y volver al carril de puertas antes de mi paradero?</summary>
        private static bool HasRoomToOvertake(in LaneDecisionInput input)
        {
            if (!input.HasNextStop) return true; // no tengo que volver a meterme: siempre puedo adelantar
            float spaceAfterLeader = input.NextStopDistanceOnRoad - input.LeftLeaderFrontDistance;
            return spaceAfterLeader >= input.RequiredMergeSpace;
        }
    }
}
