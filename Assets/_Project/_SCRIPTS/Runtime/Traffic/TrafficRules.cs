using System;
using UnityEngine;

namespace FixMyCity.Traffic
{
    /// <summary>
    /// Reglas de tráfico GLOBALES (iguales para todos los buses). Viven en el BusTrafficManager y se
    /// editan en su inspector, incluso en Play Mode, para tunear durante las pruebas.
    ///
    /// Diferencia con BusStats: BusStats describe al VEHÍCULO (cuánto acelera, cuánto mide);
    /// TrafficRules describe al REGLAMENTO (cuándo volver al carril de puertas, cuánto mirar adelante).
    /// </summary>
    [Serializable]
    public sealed class TrafficRules
    {
        [Header("Carriles")]
        [Tooltip("LANE_RETURN_DISTANCE: a cuántos metros de su próximo paradero el bus vuelve al carril de puertas.\n" +
                 "Muy bajo → no le da tiempo a cambiar y se pasa el paradero.\n" +
                 "Muy alto → se mete temprano y bloquea el carril de puertas.\n" +
                 "En la escena de prueba las estaciones están a ~140 m, por eso 60 m.")]
        [Min(5f)] public float laneReturnDistance = 60f;

        [Tooltip("Si el próximo paradero está lejos, el bus prefiere el carril expreso (doc de Mecánicas: " +
                 "'los buses cuyos siguientes paraderos sean lejanos pueden tomar el carril donde pueden acelerar').")]
        public bool preferExpressLaneWhenFar = true;

        [Tooltip("DETECTION_RANGE: metros hacia adelante que el bus 'mira' buscando otros buses.\n" +
                 "Conviene que sea MAYOR que laneReturnDistance: así ve la cola del paradero antes de decidir volver.")]
        [Min(5f)] public float detectionRange = 80f;

        [Tooltip("Margen extra (m) exigido para adelantar a un bus detenido y aún poder volver al carril de puertas antes de mi paradero.")]
        [Min(0f)] public float mergeMargin = 1f;

        [Tooltip("Cada cuántos segundos re-evalúa su carril (no hace falta cada frame; ahorra CPU y evita titubeos).")]
        [Min(0.05f)] public float laneDecisionInterval = 0.2f;

        [Tooltip("Tiempo mínimo (s) entre dos cambios de carril voluntarios. Evita el zigzag.")]
        [Min(0f)] public float minTimeBetweenLaneChanges = 2f;

        [Tooltip("Qué tan cerca (en fracción del ancho de carril) del centro de un carril debe estar un bus para " +
                 "considerarse 'dentro' de ese carril. 0.75 → a mitad del cambio ocupa AMBOS carriles (realista).")]
        [Range(0.5f, 1f)] public float laneOccupancyFactor = 0.75f;

        [Tooltip("Por debajo de esta velocidad (m/s) un bus se considera 'detenido' para decidir adelantarlo.")]
        [Min(0f)] public float stoppedSpeedThreshold = 1f;

        [Header("Simulación")]
        [Tooltip("Paso máximo de simulación (s). Si un frame dura más (tirón, o tiempo acelerado x4), se divide en sub-pasos " +
                 "para que los buses no 'atraviesen' a otros.")]
        [Min(0.005f)] public float maxSimulationStep = 1f / 30f;

        [Tooltip("Tope de sub-pasos por frame (protege el rendimiento en celulares lentos).")]
        [Range(1, 8)] public int maxSubSteps = 4;
    }
}
