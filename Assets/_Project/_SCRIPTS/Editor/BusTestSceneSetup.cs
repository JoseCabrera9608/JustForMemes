using System.Collections.Generic;
using FixMyCity.Buses;
using FixMyCity.Pooling;
using FixMyCity.Roads;
using FixMyCity.Routes;
using FixMyCity.Stations;
using FixMyCity.Traffic;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace FixMyCity.EditorTools
{
    /// <summary>
    /// Menú "Fix My City > Setup Bus Test Scene".
    ///
    /// Monta TODO lo necesario en PrototipeScene con un clic, para que al hacer pull solo tengas que
    /// abrir la escena, ejecutar el menú y darle Play. Es idempotente: puedes ejecutarlo varias veces,
    /// reutiliza lo que ya existe (no duplica) y todo se puede deshacer con Ctrl+Z.
    ///
    /// Qué hace:
    ///   1. Crea el asset BusStats_Default (si no existe).
    ///   2. Añade BusMovementController al prefab del bus.
    ///   3. Crea la vía "RoadPath_North" con sus waypoints (si no existe).
    ///   4. Añade Station a cada objeto "E-xxx" y BusStop a cada "Paradero..." hijo.
    ///   5. Crea "BUS_SYSTEM" con BusTrafficManager + BusPool + RouteManager y 4 rutas de prueba.
    ///   6. Desactiva los buses colocados a mano en la escena (ahora salen del pool).
    /// </summary>
    public static class BusTestSceneSetup
    {
        private const string BusPrefabPath = "Assets/_Project/_PREFABS/BusPrefab.prefab";
        private const string DataFolderParent = "Assets/_Project";
        private const string DataFolderName = "Data";
        private const string StatsPath = DataFolderParent + "/" + DataFolderName + "/BusStats_Default.asset";

        private const string SystemRootName = "BUS_SYSTEM";
        private const string RoadName = "RoadPath_North";

        // Medidas tomadas de PrototipeScene: los dos buses estaban colocados en x = 14.45 (carril de
        // puertas, junto a los paraderos en x = 10.5) y x = 21.25 (carril expreso). El eje queda en medio.
        private const float LeftLaneX = 14.45f;
        private const float RightLaneX = 21.25f;
        private const float RoadStartZ = -180f;
        private const float RoadEndZ = 190f;

        [MenuItem("Fix My City/Setup Bus Test Scene")]
        public static void Setup()
        {
            Scene scene = SceneManager.GetActiveScene();
            if (!scene.IsValid())
            {
                Debug.LogError("[Setup] No hay escena activa.");
                return;
            }

            BusStats stats = GetOrCreateStats();
            BusMovementController busPrefab = SetupBusPrefab(stats);
            if (busPrefab == null) return;

            Undo.IncrementCurrentGroup();
            Undo.SetCurrentGroupName("Setup Bus Test Scene");
            int undoGroup = Undo.GetCurrentGroup();

            RoadPath road = SetupRoad(scene);
            Dictionary<string, BusStop> stops = SetupStations(scene, road);
            GameObject systemRoot = SetupSystems(scene, busPrefab, road, stops);
            int disabledBuses = DisableHandPlacedBuses(scene, busPrefab.gameObject);

            Undo.CollapseUndoOperations(undoGroup);
            EditorSceneManager.MarkSceneDirty(scene);
            Selection.activeGameObject = systemRoot;

            Debug.Log($"[Setup] Listo ✔  Vía: {road.name} ({road.Length:0} m) · Paraderos: {stops.Count} · " +
                      $"Buses de escena desactivados: {disabledBuses}. Guarda la escena (Ctrl+S) y dale Play.");
        }

        // ------------------------------------------------------------------ 1. Stats

        private static BusStats GetOrCreateStats()
        {
            var stats = AssetDatabase.LoadAssetAtPath<BusStats>(StatsPath);
            if (stats != null) return stats;

            string folder = DataFolderParent + "/" + DataFolderName;
            if (!AssetDatabase.IsValidFolder(folder)) AssetDatabase.CreateFolder(DataFolderParent, DataFolderName);

            stats = ScriptableObject.CreateInstance<BusStats>();
            AssetDatabase.CreateAsset(stats, StatsPath);
            AssetDatabase.SaveAssets();
            Debug.Log($"[Setup] Creado {StatsPath}");
            return stats;
        }

        // ------------------------------------------------------------------ 2. Prefab

        private static BusMovementController SetupBusPrefab(BusStats stats)
        {
            GameObject root = PrefabUtility.LoadPrefabContents(BusPrefabPath);
            if (root == null)
            {
                Debug.LogError($"[Setup] No encontré el prefab en {BusPrefabPath}");
                return null;
            }

            try
            {
                var controller = root.GetComponent<BusMovementController>();
                if (controller == null) controller = root.AddComponent<BusMovementController>();
                if (controller.Stats == null) controller.SetStats(stats);

                // Asignar puertas explícitamente (el controller también las busca por nombre como respaldo).
                Transform d1 = root.transform.Find("Puerta1");
                Transform d2 = root.transform.Find("Puerta2");
                if (d1 != null && d2 != null)
                {
                    var so = new SerializedObject(controller);
                    SerializedProperty doors = so.FindProperty("doors");
                    doors.arraySize = 2;
                    doors.GetArrayElementAtIndex(0).objectReferenceValue = d1;
                    doors.GetArrayElementAtIndex(1).objectReferenceValue = d2;
                    so.ApplyModifiedPropertiesWithoutUndo();
                }

                PrefabUtility.SaveAsPrefabAsset(root, BusPrefabPath);
            }
            finally
            {
                PrefabUtility.UnloadPrefabContents(root);
            }

            var asset = AssetDatabase.LoadAssetAtPath<GameObject>(BusPrefabPath);
            return asset != null ? asset.GetComponent<BusMovementController>() : null;
        }

        // ------------------------------------------------------------------ 3. Vía

        private static RoadPath SetupRoad(Scene scene)
        {
            GameObject existing = FindInScene(scene, RoadName);
            if (existing != null && existing.TryGetComponent(out RoadPath existingRoad))
            {
                existingRoad.Bake();
                return existingRoad; // respetamos los ajustes que hayas hecho a mano
            }

            float centerX = (LeftLaneX + RightLaneX) * 0.5f;
            float halfWidth = (RightLaneX - LeftLaneX) * 0.5f;

            var roadGo = new GameObject(RoadName);
            Undo.RegisterCreatedObjectUndo(roadGo, "Create RoadPath");
            GameObject roadParent = FindInScene(scene, "ROAD");
            if (roadParent != null) Undo.SetTransformParent(roadGo.transform, roadParent.transform, "Parent RoadPath");
            roadGo.transform.position = Vector3.zero;

            // 3 waypoints: inicio, centro y fin. En recta bastarían 2; el del medio queda listo para
            // que pruebes curvas moviéndolo.
            float[] zs = { RoadStartZ, (RoadStartZ + RoadEndZ) * 0.5f, RoadEndZ };
            for (int i = 0; i < zs.Length; i++)
            {
                var wp = new GameObject($"WP_{i:00}");
                Undo.RegisterCreatedObjectUndo(wp, "Create Waypoint");
                wp.transform.SetParent(roadGo.transform, false);
                wp.transform.position = new Vector3(centerX, 0f, zs[i]);
            }

            RoadPath road = Undo.AddComponent<RoadPath>(roadGo);
            var so = new SerializedObject(road);
            so.FindProperty("leftLaneOffset").floatValue = -halfWidth;
            so.FindProperty("rightLaneOffset").floatValue = halfWidth;
            so.ApplyModifiedPropertiesWithoutUndo();
            road.Bake();
            return road;
        }

        // ------------------------------------------------------------------ 4. Estaciones y paraderos

        private static Dictionary<string, BusStop> SetupStations(Scene scene, RoadPath road)
        {
            var result = new Dictionary<string, BusStop>();

            foreach (GameObject rootGo in scene.GetRootGameObjects())
            {
                if (!rootGo.name.StartsWith("E-")) continue;

                if (!rootGo.TryGetComponent(out Station station))
                {
                    station = Undo.AddComponent<Station>(rootGo);
                    station.Configure(rootGo.name.Substring(2), StationSize.Small, false);
                }

                string stationCode = MakeStationCode(rootGo.name);
                foreach (Transform child in rootGo.transform)
                {
                    if (!child.name.StartsWith("Paradero")) continue;

                    if (!child.TryGetComponent(out BusStop stop)) stop = Undo.AddComponent<BusStop>(child.gameObject);
                    Undo.RecordObject(stop, "Configure BusStop");

                    string id = $"{stationCode}-{MakeStopSuffix(child.name)}";
                    stop.Configure(road, id);
                    EditorUtility.SetDirty(stop);
                    result[id] = stop;
                }
            }

            return result;
        }

        /// <summary>"E-JavierPrado" → "JP", "E-Canada" → "CA".</summary>
        private static string MakeStationCode(string objectName)
        {
            string raw = objectName.StartsWith("E-") ? objectName.Substring(2) : objectName;
            var caps = new System.Text.StringBuilder();
            foreach (char c in raw) if (char.IsUpper(c)) caps.Append(c);
            if (caps.Length >= 2) return caps.ToString();
            return raw.Length >= 2 ? raw.Substring(0, 2).ToUpperInvariant() : raw.ToUpperInvariant();
        }

        /// <summary>"ParaderoNorte1" → "N1", "ParaderoSur2" → "S2".</summary>
        private static string MakeStopSuffix(string objectName)
        {
            string dir = objectName.Contains("Norte") ? "N" : objectName.Contains("Sur") ? "S" : "P";
            var digits = new System.Text.StringBuilder();
            foreach (char c in objectName) if (char.IsDigit(c)) digits.Append(c);
            return dir + (digits.Length > 0 ? digits.ToString() : "1");
        }

        // ------------------------------------------------------------------ 5. Sistemas y rutas de prueba

        private static GameObject SetupSystems(Scene scene, BusMovementController busPrefab, RoadPath road,
                                               Dictionary<string, BusStop> stops)
        {
            GameObject systemRoot = FindInScene(scene, SystemRootName);
            if (systemRoot == null)
            {
                systemRoot = new GameObject(SystemRootName);
                Undo.RegisterCreatedObjectUndo(systemRoot, "Create Bus System");
            }

            BusTrafficManager traffic = GetOrAdd<BusTrafficManager>(systemRoot);
            BusPool pool = GetOrAdd<BusPool>(systemRoot);
            RouteManager routes = GetOrAdd<RouteManager>(systemRoot);

            Undo.RecordObject(pool, "Configure BusPool");
            pool.Configure(busPrefab, 12);
            EditorUtility.SetDirty(pool);

            Undo.RecordObject(routes, "Configure Routes");
            routes.Configure(pool, traffic);
            routes.ClearRoutes();

            // Cada ruta prueba un comportamiento concreto del spec:
            //  A  (JP-N1 → CA-N1): ruta normal. Tras embarcar en JP va por el expreso y vuelve antes de CA.
            //  B  (JP-N2 → CA-N2): si un bus de A está detenido en JP-N1, B lo adelanta para llegar a JP-N2.
            //  SX (solo CA-N2)   : Video 1 → pasa Javier Prado por el carril expreso sin frenar.
            //  C  (solo JP-N2, embarque x3): bus lento que provoca colas y adelantamientos (Video 2).
            AddRoute(routes, stops, road, "A", RouteService.Regular, new[] { "JP-N1", "CA-N1" }, 1f,
                     new Color(1f, 0.55f, 0.1f), interval: 14f, delay: 0f, max: 3);
            AddRoute(routes, stops, road, "B", RouteService.Regular, new[] { "JP-N2", "CA-N2" }, 1f,
                     new Color(0.2f, 0.8f, 1f), interval: 14f, delay: 5f, max: 3);
            AddRoute(routes, stops, road, "SX", RouteService.Express, new[] { "CA-N2" }, 1f,
                     new Color(1f, 0.2f, 1f), interval: 20f, delay: 9f, max: 2);
            AddRoute(routes, stops, road, "C-Lenta", RouteService.Regular, new[] { "JP-N2" }, 3f,
                     new Color(1f, 0.9f, 0.2f), interval: 22f, delay: 2f, max: 2);

            EditorUtility.SetDirty(routes);
            return systemRoot;
        }

        private static void AddRoute(RouteManager manager, Dictionary<string, BusStop> stops, RoadPath road,
                                     string name, RouteService service, string[] stopIds, float boardingMultiplier,
                                     Color color, float interval, float delay, int max)
        {
            var routeStops = new List<BusStop>();
            foreach (string id in stopIds)
            {
                if (stops.TryGetValue(id, out BusStop stop)) routeStops.Add(stop);
                else Debug.LogWarning($"[Setup] Ruta {name}: no encontré el paradero {id}.");
            }

            if (routeStops.Count == 0) return;
            var route = new BusRoute(name, service, road, routeStops, boardingMultiplier, color);
            manager.AddRoute(route, interval, delay, max);
        }

        // ------------------------------------------------------------------ 6. Buses colocados a mano

        private static int DisableHandPlacedBuses(Scene scene, GameObject busPrefabAsset)
        {
            int count = 0;
            foreach (GameObject rootGo in scene.GetRootGameObjects())
            {
                foreach (Transform t in rootGo.GetComponentsInChildren<Transform>(true))
                {
                    GameObject go = t.gameObject;
                    if (!go.activeSelf) continue;
                    if (!PrefabUtility.IsOutermostPrefabInstanceRoot(go)) continue;
                    if (PrefabUtility.GetCorrespondingObjectFromSource(go) != busPrefabAsset) continue;

                    Undo.RecordObject(go, "Disable scene bus");
                    go.SetActive(false);
                    count++;
                }
            }
            return count;
        }

        // ------------------------------------------------------------------ Utilidades

        private static T GetOrAdd<T>(GameObject go) where T : Component
        {
            return go.TryGetComponent(out T existing) ? existing : Undo.AddComponent<T>(go);
        }

        private static GameObject FindInScene(Scene scene, string objectName)
        {
            foreach (GameObject rootGo in scene.GetRootGameObjects())
            {
                if (rootGo.name == objectName) return rootGo;
                foreach (Transform t in rootGo.GetComponentsInChildren<Transform>(true))
                    if (t.name == objectName) return t.gameObject;
            }
            return null;
        }
    }
}
