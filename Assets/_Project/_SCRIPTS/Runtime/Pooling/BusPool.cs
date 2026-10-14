using System.Collections.Generic;
using FixMyCity.Buses;
using UnityEngine;
using UnityEngine.Pool;

namespace FixMyCity.Pooling
{
    /// <summary>
    /// Pool de buses. En vez de Instantiate/Destroy cada vez que sale o termina un bus, reciclamos
    /// instancias desactivadas.
    ///
    /// ¿Por qué importa en celular?
    ///   - Instantiate es caro (copia jerarquía, componentes, mallas) → tirones al spawnear.
    ///   - Destroy genera basura → el Garbage Collector se dispara y congela frames.
    /// Con "prewarm" creamos todos al cargar la escena (pantalla de carga) y durante la partida
    /// solo hacemos SetActive(true/false).
    ///
    /// Usamos UnityEngine.Pool.ObjectPool (viene con Unity, probado y sin asignaciones extra).
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class BusPool : MonoBehaviour
    {
        [SerializeField] private BusMovementController prefab;
        [Tooltip("Buses creados al iniciar. Pon aquí el máximo esperado de buses simultáneos.")]
        [SerializeField, Min(0)] private int prewarmCount = 12;
        [Tooltip("Máximo de buses inactivos guardados. Si se supera, los sobrantes se destruyen.")]
        [SerializeField, Min(1)] private int maxPoolSize = 64;

        private ObjectPool<BusMovementController> _pool;
        private int _createdCount;

        /// <summary>Stats del prefab (para calcular espacio de spawn sin sacar un bus del pool).</summary>
        public BusStats PrefabStats => prefab != null ? prefab.Stats : null;

        public int CountActive => _pool?.CountActive ?? 0;
        public int CountInactive => _pool?.CountInactive ?? 0;

        private void Awake()
        {
            if (prefab == null)
            {
                Debug.LogError("[BusPool] Falta asignar el prefab del bus.", this);
                enabled = false;
                return;
            }

            _pool = new ObjectPool<BusMovementController>(
                createFunc: CreateBus,
                actionOnGet: bus => bus.gameObject.SetActive(true),
                actionOnRelease: bus =>
                {
                    bus.ReleaseRoute();
                    bus.gameObject.SetActive(false);
                },
                actionOnDestroy: bus => { if (bus != null) Destroy(bus.gameObject); },
                collectionCheck: Application.isEditor, // detecta doble Release en editor; gratis en build
                defaultCapacity: prewarmCount,
                maxSize: maxPoolSize);

            Prewarm();
        }

        private void Prewarm()
        {
            var temp = new List<BusMovementController>(prewarmCount);
            for (int i = 0; i < prewarmCount; i++) temp.Add(_pool.Get());
            for (int i = 0; i < temp.Count; i++) _pool.Release(temp[i]);
        }

        private BusMovementController CreateBus()
        {
            BusMovementController bus = Instantiate(prefab, transform);
            bus.name = $"Bus_{_createdCount++:00}";
            bus.gameObject.SetActive(false);
            return bus;
        }

        public BusMovementController Get() => _pool?.Get();

        public void Release(BusMovementController bus)
        {
            if (bus == null || _pool == null) return;
            _pool.Release(bus);
        }

        /// <summary>API de construcción (herramienta de setup).</summary>
        public void Configure(BusMovementController busPrefab, int prewarm)
        {
            prefab = busPrefab;
            prewarmCount = Mathf.Max(0, prewarm);
        }

        private void OnDestroy()
        {
            _pool?.Dispose();
        }
    }
}
