//This file is part of WUIPlatform Copyright (C) 2024 Jonathan Wahlqvist
//WUIPlatform is free software: you can redistribute it and/or modify it under the terms of the GNU General Public License as published by
//the Free Software Foundation, either version 3 of the License, or (at your option) any later version.
//This program is distributed in the hope that it will be useful, but WITHOUT ANY WARRANTY; without even the implied warranty of
//MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the GNU General Public License for more details.
//You should have received a copy of the GNU General Public License along with this program.  If not, see <http://www.gnu.org/licenses/>.

using UnityEngine;
using PREACT.Pedestrian;
using System.Collections.Generic;
using PREACT;
using PREACT.Traffic;
using PREACT.Math;
using System;
using System.IO;
using System.Reflection;
using Unity.VectorGraphics;
using Mathf = PREACT.Math.Mathf;

namespace WUInity.Visualization
{
    public class EvacuationRenderer : MonoBehaviour
    {
        [SerializeField] Material householdsMaterial;
        [SerializeField] Mesh householdMesh;
        [SerializeField] Material carsMaterial;
        [SerializeField] Mesh carMesh;

        Bounds bounds;
        ComputeBuffer householdPositionsBuffer;        
        ComputeBuffer carPositionsBuffer;
        Dictionary<uint, TrafficModuleVehicle> _activeVehicles;
        private readonly List<GameObject> _droneMarkers = new List<GameObject>();
        private readonly List<LineRenderer> _focusedCellLines = new List<LineRenderer>();
        private readonly List<LineRenderer> _sumoRoadLines = new List<LineRenderer>();
        private bool _sumoRoadsBuilt;
        private Vector2d _domainSize;
        private static Sprite _droneSprite;
        private static bool _droneSpriteLoaded;
        private float _nextDroneVizLogTime;


        public void CreateBuffers(bool renderHouseholds, bool renderTraffic, Vector2d domainSize, PedestrianModule pedestrianModule)
        {
            Release();

            //calculate bounds here as traffic will need it too, not only pedestrian visualizer
            Vector3 center = new Vector3((float)domainSize.x * 0.5f, 1f, (float)domainSize.y * 0.5f);
            Vector3 size = new Vector3((float)domainSize.x + 2f, 2f, (float)domainSize.y + 2f);
            bounds = new Bounds(center, size);
            _domainSize = domainSize;
            _sumoRoadsBuilt = false;
            ClearRoadLines();
            ClearDroneMarkers();
            ClearFocusedCellLines();

            if (renderHouseholds)
            {
                CreateHouseholdsBuffer(((MacroHouseholdSim)pedestrianModule).GetHouseholdPositions().Length);
            }            
        }

        private void CreateHouseholdsBuffer(int householdCount)
        {            
            if(householdCount > 0)
            {
                householdPositionsBuffer = new ComputeBuffer(householdCount, 4 * sizeof(float));
            }
        }

        public void UpdateEvacuationRenderer(bool renderHouseholds, bool renderCars, PedestrianModule pedestrianModule, TrafficModule trafficModule, Simulation simulation)
        {
            if (renderHouseholds && householdPositionsBuffer != null)
            {
                System.Numerics.Vector4[] newPositions = ((MacroHouseholdSim)pedestrianModule).GetHouseholdPositions();
                householdPositionsBuffer.SetData(newPositions);
                householdsMaterial.SetBuffer("_PositionsAndState", householdPositionsBuffer);
                Graphics.DrawMeshInstancedProcedural(householdMesh, 0, householdsMaterial, bounds, householdPositionsBuffer.count, null, UnityEngine.Rendering.ShadowCastingMode.Off, false, 0, null, UnityEngine.Rendering.LightProbeUsage.Off, null);
            }

            if (renderCars && trafficModule != null)
            {
                Dictionary<uint, TrafficModuleVehicle> currentVehicles = trafficModule.GetActiveVehicles();
                if(currentVehicles.Count > 0)
                {
                    //need to make a copy as it might get modified during foreach
                    _activeVehicles = new Dictionary<uint, TrafficModuleVehicle>(currentVehicles);

                    if (carPositionsBuffer == null || _activeVehicles.Count != carPositionsBuffer.count)
                    {     
                        if (carPositionsBuffer != null)
                        {
                            carPositionsBuffer.Release();
                        }
                        carPositionsBuffer = new ComputeBuffer(_activeVehicles.Count, 4 * sizeof(float));
                    }
                    
                    List<Vector4> dataToRender = new List<Vector4>();
                    foreach(TrafficModuleVehicle vehicle in _activeVehicles.Values)
                    {
                        Vector2d pos = vehicle.SimulationPos;
                        float speedRatio = vehicle.SpeedRatio;
                        Vector4 data = new Vector4((float)pos.x, (float)pos.y, speedRatio, 0f);
                        dataToRender.Add(data);
                    }
                    carPositionsBuffer.SetData(dataToRender);
                    carsMaterial.SetBuffer("_PositionsAndState", carPositionsBuffer);
                    Graphics.DrawMeshInstancedProcedural(carMesh, 0, carsMaterial, bounds, carPositionsBuffer.count, null, UnityEngine.Rendering.ShadowCastingMode.Off, false, 0, null, UnityEngine.Rendering.LightProbeUsage.Off, null);
                }           
            }

            UpdateDroneAndFocusedCellVisuals(trafficModule, simulation);
        }

        public void BuildSumoRoadOverlay(string sumoConfigPath, Simulation simulation)
        {
            ClearRoadLines();
            _sumoRoadsBuilt = true;
            if (simulation == null || string.IsNullOrEmpty(sumoConfigPath))
            {
                return;
            }

            string cfgPath = Path.IsPathRooted(sumoConfigPath)
                ? sumoConfigPath
                : Path.Combine(simulation.Engine.WorkingFolder, sumoConfigPath);
            if (!File.Exists(cfgPath))
            {
                return;
            }

            SumoConfig config = new SumoConfig(cfgPath, true);
            Vector2d sumoUTM = -config.Network.UTMOffset;
            Vector2d originOffset = sumoUTM - simulation.Spatial.UTMOrigin;

            foreach (SumoEdge edge in config.Network.Edges.Values)
            {
                if (edge.Shape == null || edge.Shape.Count < 2)
                {
                    continue;
                }

                List<Vector3> points = new List<Vector3>(edge.Shape.Count);
                for (int i = 0; i < edge.Shape.Count; i++)
                {
                    Vector2d p = new Vector2d(edge.Shape[i].x, edge.Shape[i].y) + originOffset;
                    points.Add(new Vector3((float)p.x, 0.5f, (float)p.y));
                }

                if (points.Count < 2)
                {
                    continue;
                }

                List<Vector3> clipped = ClipPolylineToDomain(points);
                if (clipped.Count < 2)
                {
                    continue;
                }

                LineRenderer line = CreateLineRenderer("SUMO Road", new Color(1f, 0.55f, 0f), 4.0f, 0.5f);
                line.positionCount = clipped.Count;
                line.SetPositions(clipped.ToArray());
                _sumoRoadLines.Add(line);
            }
        }

        private void UpdateDroneAndFocusedCellVisuals(TrafficModule trafficModule, Simulation simulation)
        {
            object droneModule = GetDroneModule(simulation);
            if (droneModule == null)
            {
                EnsureCount(_droneMarkers, 0, CreateDroneMarker, DestroyMarker);
                return;
            }
            Vector2d[] positions = GetDronePositions(droneModule);
            EnsureCount(_droneMarkers, positions.Length, CreateDroneMarker, DestroyMarker);

            for (int i = 0; i < positions.Length; i++)
            {
                _droneMarkers[i].SetActive(true);
                _droneMarkers[i].transform.position = new Vector3((float)positions[i].x, 30f, (float)positions[i].y);
            }

            DrawActiveRasterCells(droneModule);

            if (Time.unscaledTime >= _nextDroneVizLogTime)
            {
                string moduleName = droneModule != null ? droneModule.GetType().FullName : "null";
                int activeMaskCount = CountActiveMaskCells(droneModule);
                Debug.Log($"[WUInity TEST][DroneViz] module={moduleName} positions={positions.Length} markers={_droneMarkers.Count} activeMask={activeMaskCount} spriteLoaded={_droneSpriteLoaded} spriteNull={_droneSprite == null}");
                if (positions.Length > 0)
                {
                    Vector2d p = positions[0];
                    Debug.Log($"[WUInity][DroneViz] firstPos=({p.x:0.00},{p.y:0.00})");
                }
                _nextDroneVizLogTime = Time.unscaledTime + 2f;
            }
        }

        private static void EnsureCount<T>(List<T> list, int targetCount, Func<T> create, Action<T> destroy)
        {
            while (list.Count < targetCount)
            {
                list.Add(create());
            }

            while (list.Count > targetCount)
            {
                int last = list.Count - 1;
                destroy(list[last]);
                list.RemoveAt(last);
            }
        }

        private GameObject CreateDroneMarker()
        {
            GameObject marker = new GameObject("DroneMarker");
            marker.name = "DroneMarker";
            marker.transform.SetParent(transform, false);

            GameObject dot = GameObject.CreatePrimitive(PrimitiveType.Sphere);
            dot.transform.SetParent(marker.transform, false);
            dot.transform.localScale = new Vector3(70f, 70f, 70f);
            Collider c = dot.GetComponent<Collider>();
            if (c != null)
            {
                Destroy(c);
            }

            Renderer renderer = dot.GetComponent<Renderer>();
            if (renderer != null)
            {
                Material material = new Material(Shader.Find("Standard"));
                // Configure the Standard shader for transparent rendering. Setting the
                // colour alpha alone has no effect unless the material is switched into
                // its transparent blend mode with the matching keywords and render queue.
                material.SetFloat("_Mode", 3f); // 3 = Transparent
                material.SetInt("_SrcBlend", (int)UnityEngine.Rendering.BlendMode.SrcAlpha);
                material.SetInt("_DstBlend", (int)UnityEngine.Rendering.BlendMode.OneMinusSrcAlpha);
                material.SetInt("_ZWrite", 0);
                material.DisableKeyword("_ALPHATEST_ON");
                material.EnableKeyword("_ALPHABLEND_ON");
                material.DisableKeyword("_ALPHAPREMULTIPLY_ON");
                material.renderQueue = (int)UnityEngine.Rendering.RenderQueue.Transparent;
                material.color = new Color(1f, 0f, 1f, 0.45f); // magenta, semi-transparent
                renderer.material = material;
            }

            return marker;
        }

        private static Sprite GetOrBuildDroneSprite()
        {
            if (_droneSpriteLoaded)
            {
                return _droneSprite;
            }

            _droneSpriteLoaded = true;
            _droneSprite = Resources.Load<Sprite>("Drone/quadcopter_drone");
            if (_droneSprite != null)
            {
                Debug.Log("[WUInity][DroneViz] Loaded drone sprite from Resources Sprite.");
                return _droneSprite;
            }

            try
            {
                string svgPath = Path.Combine(Application.dataPath, "WUInity/Resources/Drone/quadcopter_drone.svg");
                if (!File.Exists(svgPath))
                {
                    Debug.LogWarning("[WUInity][DroneViz] SVG file not found at: " + svgPath);
                    return null;
                }

                string svgText = File.ReadAllText(svgPath);
                using (StringReader reader = new StringReader(svgText))
                {
                    SVGParser.SceneInfo sceneInfo = SVGParser.ImportSVG(reader);
                    var tessOptions = new VectorUtils.TessellationOptions
                    {
                        StepDistance = 8.0f,
                        MaxCordDeviation = 0.5f,
                        MaxTanAngleDeviation = 0.1f,
                        SamplingStepSize = 0.01f
                    };
                    var geoms = VectorUtils.TessellateScene(sceneInfo.Scene, tessOptions);
                    _droneSprite = VectorUtils.BuildSprite(
                        geoms,
                        1024.0f,
                        VectorUtils.Alignment.Center,
                        Vector2.zero,
                        128,
                        true);
                    Debug.Log("[WUInity][DroneViz] Built drone sprite from raw SVG text.");
                }
            }
            catch
            {
                _droneSprite = null;
                Debug.LogWarning("[WUInity][DroneViz] Exception while building SVG sprite.");
            }

            if (_droneSprite == null)
            {
                Debug.LogWarning("[WUInity][DroneViz] Drone sprite is null, using fallback sphere.");
            }

            return _droneSprite;
        }

        private static int CountActiveMaskCells(object droneModule)
        {
            bool[] mask = GetRasterMask(droneModule);
            int count = 0;
            for (int i = 0; i < mask.Length; i++)
            {
                if (mask[i])
                {
                    count++;
                }
            }
            return count;
        }

        private static void DestroyMarker(GameObject marker)
        {
            if (marker != null)
            {
                Destroy(marker);
            }
        }

        private LineRenderer CreateFocusedCellLine()
        {
            return CreateLineRenderer("DroneFocusedCell", Color.cyan, 6f, 0.2f);
        }

        private LineRenderer CreateLineRenderer(string name, Color color, float width, float yOffset)
        {
            GameObject go = new GameObject(name);
            go.transform.SetParent(transform, false);
            LineRenderer line = go.AddComponent<LineRenderer>();
            line.useWorldSpace = true;
            line.material = new Material(Shader.Find("Sprites/Default"));
            line.startColor = color;
            line.endColor = color;
            line.startWidth = width;
            line.endWidth = width;
            line.loop = false;
            go.transform.position = new Vector3(0f, yOffset, 0f);
            return line;
        }

        private static void DestroyLine(LineRenderer line)
        {
            if (line != null)
            {
                Destroy(line.gameObject);
            }
        }

        private static void SetCellOutline(LineRenderer line, Vector2d center, float halfSize)
        {
            Vector3[] corners = new Vector3[5];
            corners[0] = new Vector3((float)(center.x - halfSize), 0.2f, (float)(center.y - halfSize));
            corners[1] = new Vector3((float)(center.x + halfSize), 0.2f, (float)(center.y - halfSize));
            corners[2] = new Vector3((float)(center.x + halfSize), 0.2f, (float)(center.y + halfSize));
            corners[3] = new Vector3((float)(center.x - halfSize), 0.2f, (float)(center.y + halfSize));
            corners[4] = corners[0];
            line.positionCount = corners.Length;
            line.SetPositions(corners);
        }

        private static object GetDroneModule(Simulation simulation)
        {
            if (simulation == null || simulation.Evacuation == null)
            {
                return null;
            }

            object module = GetMemberValue(simulation.Evacuation, "DroneModule")
                ?? GetMemberValue(simulation.Evacuation, "_droneModule");
            if (module == null)
            {
                module = GetMemberValue(simulation, "Detection") != null
                    ? GetMemberValue(GetMemberValue(simulation, "Detection"), "DroneModule")
                    : null;
            }
            return module;
        }

        private static Vector2d[] GetDronePositions(object droneModule)
        {
            if (droneModule == null)
            {
                return Array.Empty<Vector2d>();
            }

            MethodInfo tryGet = droneModule.GetType().GetMethod("TryGetDronePositions", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            if (tryGet != null)
            {
                object[] args = new object[] { null };
                object ok = tryGet.Invoke(droneModule, args);
                if (ok is bool success && success && args[0] is Vector2d[] arr)
                {
                    return arr;
                }
            }

            return Array.Empty<Vector2d>();
        }

        private void DrawActiveRasterCells(object droneModule)
        {
            if (droneModule == null)
            {
                ClearFocusedCellLines();
                return;
            }

            if (!TryGetRasterGrid(droneModule, out Vector2d min, out Vector2d max, out int rows, out int cols))
            {
                ClearFocusedCellLines();
                return;
            }

            bool[] mask = GetRasterMask(droneModule);
            if (mask.Length != rows * cols)
            {
                ClearFocusedCellLines();
                return;
            }

            List<int> active = new List<int>();
            for (int i = 0; i < mask.Length; i++)
            {
                if (mask[i]) active.Add(i);
            }

            EnsureCount(_focusedCellLines, active.Count, CreateFocusedCellLine, DestroyLine);

            float cellW = (float)((max.x - min.x) / cols);
            float cellH = (float)((max.y - min.y) / rows);
            float half = 0.5f * Mathf.Min(cellW, cellH);
            for (int i = 0; i < active.Count; i++)
            {
                int idx = active[i];
                int row = idx / cols;
                int col = idx % cols;
                Vector2d center = new Vector2d(min.x + (col + 0.5) * cellW, min.y + (row + 0.5) * cellH);
                _focusedCellLines[i].gameObject.SetActive(true);
                SetCellOutline(_focusedCellLines[i], center, half);
            }
        }

        private static bool TryGetRasterGrid(object droneModule, out Vector2d min, out Vector2d max, out int rows, out int cols)
        {
            min = Vector2d.zero;
            max = Vector2d.zero;
            rows = 0;
            cols = 0;
            MethodInfo m = droneModule.GetType().GetMethod("TryGetRasterGrid", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            if (m == null)
            {
                return false;
            }

            object[] args = new object[] { min, max, rows, cols };
            object ok = m.Invoke(droneModule, args);
            if (ok is bool success && success)
            {
                min = (Vector2d)args[0];
                max = (Vector2d)args[1];
                rows = (int)args[2];
                cols = (int)args[3];
                return true;
            }

            return false;
        }

        private static bool[] GetRasterMask(object droneModule)
        {
            MethodInfo m = droneModule.GetType().GetMethod("TryGetRasterActiveMask", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            if (m == null)
            {
                return Array.Empty<bool>();
            }

            object[] args = new object[] { null };
            object ok = m.Invoke(droneModule, args);
            if (ok is bool success && success && args[0] is bool[] mask)
            {
                return mask;
            }

            return Array.Empty<bool>();
        }

        private static bool TryGetSimulationPosition(object source, out Vector2d pos)
        {
            object raw = GetMemberValue(source, "SimulationPos") ?? GetMemberValue(source, "Position") ?? GetMemberValue(source, "SimulationPosition");
            if (raw is Vector2d v)
            {
                pos = v;
                return true;
            }

            if (raw != null)
            {
                object x = GetMemberValue(raw, "x") ?? GetMemberValue(raw, "X");
                object y = GetMemberValue(raw, "y") ?? GetMemberValue(raw, "Y");
                if (TryToDouble(x, out double xd) && TryToDouble(y, out double yd))
                {
                    pos = new Vector2d(xd, yd);
                    return true;
                }
            }

            pos = Vector2d.zero;
            return false;
        }

        private static bool TryGetFocusedCellCenter(object drone, out Vector2d center, out float cellHalfSize)
        {
            object cell = GetMemberValue(drone, "FocusedCell") ?? GetMemberValue(drone, "FocusCell") ?? GetMemberValue(drone, "CurrentCell");
            if (cell == null)
            {
                center = Vector2d.zero;
                cellHalfSize = 0f;
                return false;
            }

            if (TryGetSimulationPosition(cell, out center))
            {
                object sizeRaw = GetMemberValue(cell, "Size") ?? GetMemberValue(cell, "CellSize");
                if (TryToDouble(sizeRaw, out double size))
                {
                    cellHalfSize = PREACT.Math.Mathf.Max(1f, 0.5f * (float)size);
                    return true;
                }

                cellHalfSize = 10f;
                return true;
            }

            object ix = GetMemberValue(cell, "X") ?? GetMemberValue(cell, "x");
            object iy = GetMemberValue(cell, "Y") ?? GetMemberValue(cell, "y");
            object sizeCell = GetMemberValue(cell, "Size") ?? GetMemberValue(cell, "CellSize");
            if (TryToDouble(ix, out double x) && TryToDouble(iy, out double y))
            {
                double size = 20.0;
                if (TryToDouble(sizeCell, out double parsedSize))
                {
                    size = parsedSize;
                }

                center = new Vector2d(x * size + 0.5 * size, y * size + 0.5 * size);
                cellHalfSize = PREACT.Math.Mathf.Max(1f, 0.5f * (float)size);
                return true;
            }
            
            Console.WriteLine("Could not determine focused cell center for drone visualization.");

            center = Vector2d.zero;
            cellHalfSize = 0f;
            return false;
        }

        private static object GetMemberValue(object source, string name)
        {
            if (source == null)
            {
                return null;
            }

            Type t = source.GetType();
            PropertyInfo p = t.GetProperty(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            if (p != null)
            {
                return p.GetValue(source);
            }

            FieldInfo f = t.GetField(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            if (f != null)
            {
                return f.GetValue(source);
            }

            return null;
        }

        private static object InvokeMethod(object source, string name)
        {
            if (source == null)
            {
                return null;
            }

            MethodInfo method = source.GetType().GetMethod(name, BindingFlags.Instance | BindingFlags.Public, null, Type.EmptyTypes, null);
            if (method == null)
            {
                return null;
            }

            return method.Invoke(source, null);
        }

        private static bool TryToDouble(object value, out double result)
        {
            if (value is null)
            {
                result = 0;
                return false;
            }

            try
            {
                result = Convert.ToDouble(value);
                return true;
            }
            catch
            {
                result = 0;
                return false;
            }
        }

        private List<Vector3> ClipPolylineToDomain(List<Vector3> points)
        {
            List<Vector3> clipped = new List<Vector3>();
            for (int i = 1; i < points.Count; i++)
            {
                if (ClipSegmentToRect(points[i - 1], points[i], out Vector3 c0, out Vector3 c1))
                {
                    if (clipped.Count == 0 || Vector3.Distance(clipped[clipped.Count - 1], c0) > 0.01f)
                    {
                        clipped.Add(c0);
                    }
                    clipped.Add(c1);
                }
            }
            return clipped;
        }

        private bool ClipSegmentToRect(Vector3 p0, Vector3 p1, out Vector3 c0, out Vector3 c1)
        {
            float t0 = 0f;
            float t1 = 1f;
            float dx = p1.x - p0.x;
            float dz = p1.z - p0.z;
            float minX = 0f;
            float minZ = 0f;
            float maxX = (float)_domainSize.x;
            float maxZ = (float)_domainSize.y;

            bool ok = ClipTest(-dx, p0.x - minX, ref t0, ref t1)
                && ClipTest(dx, maxX - p0.x, ref t0, ref t1)
                && ClipTest(-dz, p0.z - minZ, ref t0, ref t1)
                && ClipTest(dz, maxZ - p0.z, ref t0, ref t1);

            if (!ok)
            {
                c0 = Vector3.zero;
                c1 = Vector3.zero;
                return false;
            }

            c0 = new Vector3(p0.x + t0 * dx, 0.5f, p0.z + t0 * dz);
            c1 = new Vector3(p0.x + t1 * dx, 0.5f, p0.z + t1 * dz);
            return true;
        }

        private static bool ClipTest(float p, float q, ref float t0, ref float t1)
        {
            if (PREACT.Math.Mathf.Approximately(p, 0f))
            {
                return q >= 0f;
            }

            float r = q / p;
            if (p < 0f)
            {
                if (r > t1)
                {
                    return false;
                }
                if (r > t0)
                {
                    t0 = r;
                }
            }
            else
            {
                if (r < t0)
                {
                    return false;
                }
                if (r < t1)
                {
                    t1 = r;
                }
            }

            return true;
        }

        private void ClearDroneMarkers()
        {
            for (int i = 0; i < _droneMarkers.Count; i++)
            {
                DestroyMarker(_droneMarkers[i]);
            }
            _droneMarkers.Clear();
        }

        private void ClearFocusedCellLines()
        {
            for (int i = 0; i < _focusedCellLines.Count; i++)
            {
                DestroyLine(_focusedCellLines[i]);
            }
            _focusedCellLines.Clear();
        }

        private void ClearRoadLines()
        {
            for (int i = 0; i < _sumoRoadLines.Count; i++)
            {
                DestroyLine(_sumoRoadLines[i]);
            }
            _sumoRoadLines.Clear();
        }

        void OnDisable()
        {
            Release();
        }

        void OnDestroy()
        {
            Release();
        }

        void Release()
        {
            if(householdPositionsBuffer != null)
            {
                householdPositionsBuffer.Release();
                householdPositionsBuffer = null;
            }

            if(carPositionsBuffer != null)
            {
                carPositionsBuffer.Release();
                carPositionsBuffer = null;
            }

            ClearDroneMarkers();
            ClearFocusedCellLines();
            ClearRoadLines();
        }
    }
}
