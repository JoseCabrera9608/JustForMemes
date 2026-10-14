using System.Collections.Generic;
using FixMyCity.Buses;
using FixMyCity.Roads;
using UnityEngine;

namespace FixMyCity.Traffic
{
    /// <summary>
    /// DIRECTOR DE TRÁFICO. Es el ÚNICO script con Update() del sistema de buses.
    ///
    /// ¿Por qué un manager central en vez de un Update() en cada bus?
    ///   1. Rendimiento: con 50 buses son 50 llamadas nativo→C# por frame vs. 1. En celular se nota.
    ///   2. Orden: necesitamos dos FASES para que el resultado no dependa del orden en la jerarquía:
    ///        FASE SENSE     → todos los buses miran el mundo (leen posiciones de los demás) y deciden.
    ///        FASE INTEGRATE → todos aplican su decisión (escriben su propia posición).
    ///      Si cada bus se moviera en su Update, el bus #3 vería al #2 ya movido y al #4 sin mover → bugs raros.
    ///   3. Mantiene la lista ORDENADA por distancia que hace barata la detección (ver BusDetectionSystem).
    ///
    /// No es un singleton estático: quien lo necesita (RouteManager) recibe la referencia por inspector.
    /// Así podrías tener dos escenas/mapas con managers distintos sin conflictos.
    /// </summary>
    [DefaultExecutionOrder(-100)]
    [DisallowMultipleComponent]
    public sealed class BusTrafficManager : MonoBehaviour
    {
        [SerializeField] private TrafficRules rules = new TrafficRules();

        /// <summary>Buses de UNA vía, ordenados por distancia (menor = más atrás).</summary>
        private sealed class RoadTraffic
        {
            public RoadPath Road;
            public readonly List<BusMovementController> Buses = new List<BusMovementController>(32);
        }

        private readonly List<RoadTraffic> _roads = new List<RoadTraffic>(4);
        private readonly List<BusMovementController> _pendingAdd = new List<BusMovementController>(8);
        private readonly List<BusMovementController> _pendingRemove = new List<BusMovementController>(8);
        private bool _ticking;

        public TrafficRules Rules => rules;

        public int ActiveBusCount
        {
            get
            {
                int total = 0;
                for (int i = 0; i < _roads.Count; i++) total += _roads[i].Buses.Count;
                return total;
            }
        }

        // ------------------------------------------------------------------ Registro

        public void Register(BusMovementController bus)
        {
            if (bus == null || bus.Road == null) return;
            if (_ticking) { AddUnique(_pendingAdd, bus); _pendingRemove.Remove(bus); return; }
            AddNow(bus);
        }

        /// <summary>
        /// Si se llama en plena simulación (ej. un bus terminó su ruta y vuelve al pool), la baja se
        /// difiere al final del frame para no modificar la lista mientras la recorremos.
        /// </summary>
        public void Unregister(BusMovementController bus)
        {
            if (bus == null) return;
            if (_ticking) { AddUnique(_pendingRemove, bus); _pendingAdd.Remove(bus); return; }
            RemoveNow(bus);
        }

        private void AddNow(BusMovementController bus)
        {
            RemoveNow(bus); // por si cambió de vía
            RoadTraffic traffic = GetOrCreate(bus.Road);
            // Insertar ya en su posición ordenada.
            List<BusMovementController> list = traffic.Buses;
            int insertAt = list.Count;
            while (insertAt > 0 && list[insertAt - 1].Distance > bus.Distance) insertAt--;
            list.Insert(insertAt, bus);
            ReindexFrom(list, insertAt);
        }

        private void RemoveNow(BusMovementController bus)
        {
            // Buscamos en todas las vías (son pocas) porque al liberarse el bus ya pudo perder su ruta.
            for (int r = 0; r < _roads.Count; r++)
            {
                List<BusMovementController> list = _roads[r].Buses;
                int idx = list.IndexOf(bus);
                if (idx < 0) continue;
                list.RemoveAt(idx);
                ReindexFrom(list, idx);
                return;
            }
        }

        private RoadTraffic GetOrCreate(RoadPath road)
        {
            for (int i = 0; i < _roads.Count; i++)
                if (_roads[i].Road == road) return _roads[i];

            var traffic = new RoadTraffic { Road = road };
            _roads.Add(traffic);
            return traffic;
        }

        /// <summary>Lista ordenada de buses de una vía (solo lectura conceptual: no la modifiques desde fuera).</summary>
        public List<BusMovementController> GetBusesOnRoad(RoadPath road)
        {
            for (int i = 0; i < _roads.Count; i++)
                if (_roads[i].Road == road) return _roads[i].Buses;
            return null;
        }

        /// <summary>¿Puedo spawnear un bus en este punto sin solaparme con otro?</summary>
        public bool IsSpaceFree(RoadPath road, float distance, float halfLength, BusLane lane, float clearance)
        {
            return BusDetectionSystem.IsSpaceFree(GetBusesOnRoad(road), distance, halfLength, lane, road, rules, clearance);
        }

        // ------------------------------------------------------------------ Simulación

        private void Update()
        {
            float frameDt = Time.deltaTime;
            if (frameDt <= 0f) return; // pausa (timeScale = 0)

            // Sub-pasos: si el frame fue largo (tirón o tiempo acelerado x2/x4 en el futuro "Hour System"),
            // simulamos en trozos pequeños para mantener la estabilidad. Con tope para no matar el CPU.
            int steps = Mathf.Clamp(Mathf.CeilToInt(frameDt / rules.maxSimulationStep), 1, rules.maxSubSteps);
            float dt = frameDt / steps;
            float time = Time.time - frameDt;

            _ticking = true;
            for (int s = 0; s < steps; s++)
            {
                time += dt;
                Step(dt, time);
            }
            _ticking = false;

            FlushPending();
        }

        private void Step(float dt, float time)
        {
            // 1) Reordenar. Insertion sort: los buses casi nunca cambian de orden entre frames, así que la
            //    lista ya viene casi ordenada → O(n) en la práctica, sin asignar memoria.
            for (int r = 0; r < _roads.Count; r++)
            {
                List<BusMovementController> list = _roads[r].Buses;
                InsertionSortByDistance(list);
                for (int i = 0; i < list.Count; i++) list[i].SortedIndex = i;
            }

            // 2) SENSE: leer el mundo y decidir (nadie se mueve todavía).
            for (int r = 0; r < _roads.Count; r++)
            {
                List<BusMovementController> list = _roads[r].Buses;
                for (int i = 0; i < list.Count; i++) list[i].Sense(list, rules, dt, time);
            }

            // 3) INTEGRATE: aplicar movimiento. (Count fijo: las bajas durante este bucle se difieren.)
            for (int r = 0; r < _roads.Count; r++)
            {
                List<BusMovementController> list = _roads[r].Buses;
                for (int i = 0; i < list.Count; i++)
                {
                    BusMovementController bus = list[i];
                    if (!_pendingRemove.Contains(bus)) bus.Integrate(dt);
                }
            }
        }

        private void FlushPending()
        {
            for (int i = 0; i < _pendingRemove.Count; i++) RemoveNow(_pendingRemove[i]);
            _pendingRemove.Clear();

            for (int i = 0; i < _pendingAdd.Count; i++) AddNow(_pendingAdd[i]);
            _pendingAdd.Clear();
        }

        private static void InsertionSortByDistance(List<BusMovementController> list)
        {
            for (int i = 1; i < list.Count; i++)
            {
                BusMovementController key = list[i];
                float keyDistance = key.Distance;
                int j = i - 1;
                while (j >= 0 && list[j].Distance > keyDistance)
                {
                    list[j + 1] = list[j];
                    j--;
                }
                list[j + 1] = key;
            }
        }

        private static void ReindexFrom(List<BusMovementController> list, int start)
        {
            for (int i = start; i < list.Count; i++) list[i].SortedIndex = i;
        }

        private static void AddUnique(List<BusMovementController> list, BusMovementController bus)
        {
            if (!list.Contains(bus)) list.Add(bus);
        }
    }
}
