using System;
using System.Collections.Generic;
using FixMyCity.Roads;
using FixMyCity.Stations;
using UnityEngine;

namespace FixMyCity.Routes
{
    /// <summary>Tipo de servicio. Hoy es informativo (UI/debug); el comportamiento "expreso" emerge solo de tener pocas paradas.</summary>
    public enum RouteService
    {
        Regular,
        Express
    }

    /// <summary>
    /// Una ruta = una vía + la lista de paraderos donde se detiene.
    ///
    /// Es una clase [Serializable] (no ScriptableObject) porque en el juego final las rutas las CREA EL
    /// JUGADOR en runtime seleccionando estaciones. Así la misma clase sirve para rutas de prueba
    /// (configuradas en el inspector) y para rutas creadas en partida.
    ///
    /// PENDIENTE (fase siguiente): ida y vuelta con giro en U en estación central. Por eso la ruta
    /// apunta a UNA vía; cuando exista el grafo de vías, una ruta tendrá un tramo de ida y uno de vuelta.
    /// </summary>
    [Serializable]
    public sealed class BusRoute
    {
        [SerializeField] private string routeName = "Ruta";
        [SerializeField] private RouteService service = RouteService.Regular;
        [SerializeField] private RoadPath road;
        [Tooltip("Paraderos donde para. El orden no importa: se ordenan por posición en la vía al inicializar.")]
        [SerializeField] private List<BusStop> stops = new List<BusStop>();
        [Tooltip("Multiplica el tiempo de embarque. Útil para pruebas (forzar un bus lento para provocar adelantamientos).")]
        [SerializeField, Min(0.1f)] private float boardingTimeMultiplier = 1f;
        [SerializeField] private Color debugColor = Color.white;

        public string RouteName => routeName;
        public RouteService Service => service;
        public RoadPath Road => road;
        public int StopCount => stops.Count;
        public float BoardingTimeMultiplier => boardingTimeMultiplier;
        public Color DebugColor => debugColor;

        public BusRoute() { }

        public BusRoute(string name, RouteService service, RoadPath road, IEnumerable<BusStop> stops,
                        float boardingTimeMultiplier = 1f, Color? debugColor = null)
        {
            routeName = name;
            this.service = service;
            this.road = road;
            this.stops = new List<BusStop>(stops);
            this.boardingTimeMultiplier = Mathf.Max(0.1f, boardingTimeMultiplier);
            this.debugColor = debugColor ?? Color.white;
        }

        public BusStop GetStop(int index) => stops[index];

        public bool HasStopAt(BusStop stop) => stops.Contains(stop);

        public bool StopsAtStation(Station station)
        {
            for (int i = 0; i < stops.Count; i++)
                if (stops[i] != null && stops[i].Station == station) return true;
            return false;
        }

        /// <summary>
        /// Valida y ordena los paraderos por su distancia en la vía. Llamar una vez antes de asignar buses.
        /// Devuelve false (con el motivo) si la ruta no es usable, para no spawnear buses rotos.
        /// </summary>
        public bool Initialize(out string error)
        {
            if (road == null) { error = $"Ruta '{routeName}' no tiene vía."; return false; }
            if (stops == null || stops.Count == 0) { error = $"Ruta '{routeName}' no tiene paraderos."; return false; }

            for (int i = stops.Count - 1; i >= 0; i--)
            {
                if (stops[i] == null) { stops.RemoveAt(i); continue; }
                if (stops[i].Road != road)
                {
                    error = $"Ruta '{routeName}': el paradero '{stops[i].StopId}' está en otra vía.";
                    return false;
                }
            }

            if (stops.Count == 0) { error = $"Ruta '{routeName}' no tiene paraderos válidos."; return false; }

            stops.Sort((a, b) => a.DistanceOnRoad.CompareTo(b.DistanceOnRoad));
            error = null;
            return true;
        }
    }
}
