using System.Collections.Generic;
using UnityEngine;

namespace FixMyCity.Stations
{
    /// <summary>Tamaño físico de la estación (ver doc de Mecánicas).</summary>
    public enum StationSize
    {
        /// <summary>Paraderos de ida y vuelta en la misma plataforma, sin puente.</summary>
        Small,
        /// <summary>Paraderos de ida y vuelta separados por un puente.</summary>
        Large
    }

    /// <summary>
    /// Una estación = un conjunto de paraderos (BusStop). Ej.: "Estación Angamos" tiene 2 paraderos
    /// de ida y 2 de vuelta. Este componente va en el GameObject padre (E-JavierPrado, E-Canada…)
    /// y descubre sus paraderos hijos automáticamente.
    ///
    /// En esta fase solo guarda datos; la capacidad, alertas de aglomeración y el spawner de pasajeros
    /// se conectarán aquí más adelante (por eso ya existe la clase, para no reestructurar después).
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class Station : MonoBehaviour
    {
        [SerializeField] private string displayName;
        [SerializeField] private StationSize size = StationSize.Small;

        [Tooltip("Estación Central: permite la vuelta en U para pasar de ida a vuelta. " +
                 "Regla de diseño: solo una estación GRANDE puede ser central.")]
        [SerializeField] private bool isCentral;

        private readonly List<BusStop> _stops = new List<BusStop>(4);

        public string DisplayName => string.IsNullOrEmpty(displayName) ? name : displayName;
        public StationSize Size => size;
        public bool IsCentral => isCentral && size == StationSize.Large;
        public IReadOnlyList<BusStop> Stops => _stops;

        private void Awake()
        {
            GetComponentsInChildren(true, _stops);
        }

        private void OnValidate()
        {
            if (isCentral && size == StationSize.Small)
            {
                Debug.LogWarning($"[Station] '{name}': una estación pequeña no puede ser central. Se desactiva 'isCentral'.", this);
                isCentral = false;
            }
        }

        /// <summary>API de construcción (usada por la herramienta de setup y, más adelante, por el editor de mapas).</summary>
        public void Configure(string newDisplayName, StationSize newSize, bool central)
        {
            displayName = newDisplayName;
            size = newSize;
            isCentral = central && newSize == StationSize.Large;
        }
    }
}
