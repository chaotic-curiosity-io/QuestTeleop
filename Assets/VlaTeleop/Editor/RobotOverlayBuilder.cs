// VLA XR teleop — robot ghost builder (editor only).
//
// Builds a physics-free, semi-transparent H1 "ghost" from the staged URDF
// (Assets/VlaTeleop/Robots/h1_description/h1_with_hand.urdf) — a plain
// Transform hierarchy + RobotOverlayGhost joint table, driven at runtime by
// RobotOverlayDriver from the teleop server's joint-target echo (:9907).
//
// This deliberately does NOT use the Unity URDF Importer package (DevVLA's
// path): an overlay needs no ArticulationBody/colliders, and QuestTeleop
// doesn't depend on that package. Instead it replicates the two conventions
// the rest of the repo already uses, so the ghost's pose math matches DevVLA
// and urdf_rerun.py exactly:
//
//  1. ROS -> Unity frames (ROS-TCP-Connector mapping):
//       position/axis (x,y,z)ros -> (-y, z, x)unity
//       a +θ rotation about a ROS axis = a −θ rotation about the mapped axis
//       URDF rpy is fixed-axis Rz(yaw)·Ry(pitch)·Rx(roll), converted per-axis.
//  2. The URDF Importer's Collada orientation fix
//     (ColladaAssetPostProcessor.getCollada{Position,Rotation}Fix), applied
//     here at instantiation time since that postprocessor isn't in this
//     project: Z_UP -> pos (-z, y, -x), rot Euler(0,90,0)·rot. The .dae.meta
//     files copied from DevVLA carry the matching ModelImporter settings
//     (useFileScale etc.), so the mesh assets themselves import identically.
//
// Mimic joints (Inspire fingers: 45 revolute URDF joints -> 33 actuated) are
// recorded with their multiplier/offset and derived at runtime, matching
// urdf_rerun.actuated_joint_names semantics.
//
// Menu: Tools ▸ Robot Teleop ▸ Robot Overlay ▸ Build H1 Ghost / Remove.
// Verify after building: the ghost should stand upright at the origin facing
// +Z in its rest pose. If a limb points the wrong way, the frame math above
// is the only place to look.

using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Xml;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace VlaTeleop.EditorTools
{
    public static class RobotOverlayBuilder
    {
        const string UrdfPath = "Assets/VlaTeleop/Robots/h1_description/h1_with_hand.urdf";
        const string GhostRootName = "H1Ghost";
        const string GhostMatPath = "Assets/VlaTeleop/Robots/H1GhostMaterial.mat";

        // ---- URDF model ----------------------------------------------------- //

        class UrdfVisual
        {
            public string mesh;                 // relative path, e.g. "meshes/pelvis.dae"
            public Vector3 xyz, rpy;            // ROS frame
            public Vector3 scale = Vector3.one;
            public string primitive;            // "box"|"cylinder"|"sphere" if no mesh
            public Vector3 primSize = Vector3.one;   // box size / (radius,length,-) ROS
        }

        class UrdfJoint
        {
            public string name, type, parent, child;
            public Vector3 xyz, rpy;            // ROS frame
            public Vector3 axis = new Vector3(1, 0, 0);
            public string mimicJoint;
            public float mimicMult = 1f, mimicOff;
        }

        [MenuItem("Tools/Robot Teleop/Robot Overlay/Build H1 Ghost", priority = 40)]
        public static void BuildH1Ghost()
        {
            Build(UrdfPath, GhostRootName, "h1");
        }

        [MenuItem("Tools/Robot Teleop/Robot Overlay/Remove H1 Ghost", priority = 41)]
        public static void RemoveH1Ghost()
        {
            var go = GameObject.Find(GhostRootName);
            if (go == null) { Debug.Log("[RobotOverlay] No ghost in scene."); return; }
            Undo.DestroyObjectImmediate(go);
            EditorSceneManager.MarkSceneDirty(EditorSceneManager.GetActiveScene());
            Debug.Log("[RobotOverlay] Ghost removed.");
        }

        // ---- build ----------------------------------------------------------- //

        static void Build(string urdfAssetPath, string rootName, string robotName)
        {
            string absUrdf = Path.GetFullPath(urdfAssetPath);
            if (!File.Exists(absUrdf))
            {
                EditorUtility.DisplayDialog("URDF not found",
                    $"Expected the staged robot at:\n{urdfAssetPath}", "OK");
                return;
            }
            string assetDir = Path.GetDirectoryName(urdfAssetPath).Replace('\\', '/');

            var doc = new XmlDocument();
            doc.Load(absUrdf);
            XmlElement robot = doc.DocumentElement;

            var visuals = new Dictionary<string, List<UrdfVisual>>();   // link -> visuals
            var joints = new List<UrdfJoint>();
            foreach (XmlElement el in robot.ChildNodes.OfType<XmlElement>())
            {
                if (el.Name == "link") visuals[el.GetAttribute("name")] = ParseVisuals(el);
                else if (el.Name == "joint") joints.Add(ParseJoint(el));
            }
            var childLinks = new HashSet<string>(joints.Select(j => j.child));
            string rootLink = visuals.Keys.FirstOrDefault(n => !childLinks.Contains(n));
            if (rootLink == null)
            {
                Debug.LogError("[RobotOverlay] Could not find the URDF root link.");
                return;
            }

            var old = GameObject.Find(rootName);
            if (old != null)
            {
                Debug.Log("[RobotOverlay] Rebuilding — existing ghost replaced " +
                          "(driver settings reset to defaults).");
                Undo.DestroyObjectImmediate(old);
            }

            var rootGo = new GameObject(rootName);
            Undo.RegisterCreatedObjectUndo(rootGo, "Build robot ghost");
            var entries = new List<RobotOverlayGhost.JointEntry>();
            int meshCount = 0, missingMesh = 0;
            BuildLink(rootLink, rootGo.transform, Vector3.zero, Quaternion.identity,
                      visuals, joints, assetDir, entries, ref meshCount, ref missingMesh);

            // Ghost material + renderer cleanup (no shadows, no colliders).
            Material mat = GetOrCreateGhostMaterial();
            foreach (var r in rootGo.GetComponentsInChildren<Renderer>(true))
            {
                var mats = new Material[r.sharedMaterials.Length];
                for (int i = 0; i < mats.Length; i++) mats[i] = mat;
                r.sharedMaterials = mats;
                r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
                r.receiveShadows = false;
            }
            foreach (var c in rootGo.GetComponentsInChildren<Collider>(true))
                Object.DestroyImmediate(c);

            // Feet-to-pelvis from the rest-pose bounds (root is at the origin).
            float minY = 0f;
            foreach (var r in rootGo.GetComponentsInChildren<Renderer>(true))
                minY = Mathf.Min(minY, r.bounds.min.y);

            var ghost = rootGo.AddComponent<RobotOverlayGhost>();
            ghost.joints = entries.ToArray();
            ghost.feetToPelvis = -minY;
            ghost.robotName = robotName;

            var driver = rootGo.AddComponent<RobotOverlayDriver>();
            driver.ghost = ghost;
            var rig = Object.FindObjectOfType<OVRCameraRig>();
            if (rig != null)
            {
                driver.headTransform = rig.centerEyeAnchor;
                driver.trackingSpace = rig.trackingSpace;
            }
            driver.bodyAnchors = Object.FindObjectOfType<QuestBodyAnchors>();

            EditorSceneManager.MarkSceneDirty(EditorSceneManager.GetActiveScene());
            Selection.activeGameObject = rootGo;

            int actuated = entries.Count(e => !e.isMimic);
            Debug.Log($"[RobotOverlay] Built '{rootName}' from {urdfAssetPath}: " +
                      $"{actuated} actuated + {entries.Count - actuated} mimic joints, " +
                      $"{meshCount} meshes" +
                      (missingMesh > 0 ? $" ({missingMesh} MISSING — check the staged assets)" : "") +
                      $", feetToPelvis={ghost.feetToPelvis:0.###} m. " +
                      "Expected for H1: 33 actuated + 12 mimic. Rest pose should stand " +
                      "upright facing +Z. Play with the teleop server running " +
                      "(--source quest-xr) to see it move; RobotOverlayDriver listens " +
                      $"on udp :{driver.listenPort}.");
        }

        static void BuildLink(string linkName, Transform parent, Vector3 localPos,
                              Quaternion localRot,
                              Dictionary<string, List<UrdfVisual>> visuals,
                              List<UrdfJoint> joints, string assetDir,
                              List<RobotOverlayGhost.JointEntry> entries,
                              ref int meshCount, ref int missingMesh)
        {
            var go = new GameObject(linkName);
            go.transform.SetParent(parent, false);
            go.transform.localPosition = localPos;
            go.transform.localRotation = localRot;

            if (visuals.TryGetValue(linkName, out var vis))
                foreach (var v in vis)
                    AttachVisual(go.transform, v, assetDir, ref meshCount, ref missingMesh);

            foreach (var j in joints)
            {
                if (j.parent != linkName) continue;
                Vector3 pos = RosToUnity(j.xyz);
                Quaternion rot = RosRpyToUnity(j.rpy);
                BuildLink(j.child, go.transform, pos, rot, visuals, joints, assetDir,
                          entries, ref meshCount, ref missingMesh);
                if (j.type == "revolute" || j.type == "continuous")
                {
                    Transform childT = go.transform.Find(j.child);
                    entries.Add(new RobotOverlayGhost.JointEntry
                    {
                        name = j.name,
                        joint = childT,
                        axisUnity = RosToUnity(j.axis).normalized,
                        isMimic = !string.IsNullOrEmpty(j.mimicJoint),
                        mimicSource = j.mimicJoint ?? "",
                        mimicMultiplier = j.mimicMult,
                        mimicOffset = j.mimicOff,
                    });
                }
                else if (j.type != "fixed")
                {
                    Debug.LogWarning($"[RobotOverlay] Joint '{j.name}' type " +
                                     $"'{j.type}' not supported — held at rest.");
                }
            }
        }

        static void AttachVisual(Transform link, UrdfVisual v, string assetDir,
                                 ref int meshCount, ref int missingMesh)
        {
            var visGo = new GameObject("visual");
            visGo.transform.SetParent(link, false);
            visGo.transform.localPosition = RosToUnity(v.xyz);
            visGo.transform.localRotation = RosRpyToUnity(v.rpy);

            if (v.mesh != null)
            {
                string meshPath = assetDir + "/" + v.mesh.Replace('\\', '/');
                var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(meshPath);
                if (prefab == null)
                {
                    Debug.LogWarning($"[RobotOverlay] Mesh not found: {meshPath}");
                    missingMesh++;
                    return;
                }
                var inst = Object.Instantiate(prefab, visGo.transform, false);
                inst.name = prefab.name;
                foreach (var extra in inst.GetComponentsInChildren<Component>(true)
                             .Where(c => c is Camera || c is Light || c is Animation))
                    Object.DestroyImmediate(extra);
                if (meshPath.ToLowerInvariant().EndsWith(".dae"))
                    ApplyColladaFix(inst.transform, ReadDaeUpAxis(Path.GetFullPath(meshPath)));
                if (v.scale != Vector3.one)
                {
                    Vector3 s = inst.transform.localScale;
                    // URDF mesh scale is per-axis in the MESH frame; H1 uses none.
                    inst.transform.localScale = new Vector3(
                        s.x * v.scale.x, s.y * v.scale.y, s.z * v.scale.z);
                }
                meshCount++;
            }
            else if (v.primitive != null)
            {
                PrimitiveType pt = v.primitive == "box" ? PrimitiveType.Cube
                    : v.primitive == "sphere" ? PrimitiveType.Sphere
                    : PrimitiveType.Cylinder;
                var prim = GameObject.CreatePrimitive(pt);
                Object.DestroyImmediate(prim.GetComponent<Collider>());
                prim.transform.SetParent(visGo.transform, false);
                if (v.primitive == "box")
                    prim.transform.localScale = RosToUnityAbs(v.primSize);
                else if (v.primitive == "sphere")
                    prim.transform.localScale = Vector3.one * (2f * v.primSize.x);
                else   // cylinder: URDF radius/length about ROS z = Unity y ✓
                    prim.transform.localScale = new Vector3(
                        2f * v.primSize.x, v.primSize.y * 0.5f, 2f * v.primSize.x);
            }
        }

        // ---- frame conversions ------------------------------------------------ //

        static Vector3 RosToUnity(Vector3 r) => new Vector3(-r.y, r.z, r.x);

        static Vector3 RosToUnityAbs(Vector3 r) =>
            new Vector3(Mathf.Abs(r.y), Mathf.Abs(r.z), Mathf.Abs(r.x));

        /// <summary>+rad about a ROS axis == −rad about the ROS->Unity-mapped
        /// axis (right-handed to left-handed flip).</summary>
        static Quaternion RosAxisRotation(float rad, Vector3 rosAxis) =>
            Quaternion.AngleAxis(-rad * Mathf.Rad2Deg, RosToUnity(rosAxis));

        /// <summary>URDF rpy (fixed-axis): R = Rz(yaw)·Ry(pitch)·Rx(roll).</summary>
        static Quaternion RosRpyToUnity(Vector3 rpy) =>
            RosAxisRotation(rpy.z, new Vector3(0, 0, 1)) *
            RosAxisRotation(rpy.y, new Vector3(0, 1, 0)) *
            RosAxisRotation(rpy.x, new Vector3(1, 0, 0));

        // Mirror of the URDF Importer's ColladaAssetPostProcessor fix, which is
        // applied at import time in DevVLA but has no postprocessor here.
        static void ApplyColladaFix(Transform t, string upAxis)
        {
            Vector3 p = t.localPosition;
            switch (upAxis)
            {
                case "Z_UP":
                    t.localPosition = new Vector3(-p.z, p.y, -p.x);
                    t.localRotation = Quaternion.Euler(0, 90, 0) * t.localRotation;
                    break;
                case "X_UP":
                    t.localRotation = Quaternion.Euler(-90, 90, 90) * t.localRotation;
                    break;
                default:   // Y_UP / unknown
                    t.localRotation = Quaternion.Euler(-90, 90, 0) * t.localRotation;
                    break;
            }
        }

        static string ReadDaeUpAxis(string absPath)
        {
            try
            {
                string text = File.ReadAllText(absPath);
                int i = text.IndexOf("<up_axis>", System.StringComparison.Ordinal);
                if (i < 0) return "Y_UP";
                int e = text.IndexOf("</up_axis>", i, System.StringComparison.Ordinal);
                return text.Substring(i + 9, e - i - 9).Trim();
            }
            catch { return "Y_UP"; }
        }

        // ---- parsing ---------------------------------------------------------- //

        static List<UrdfVisual> ParseVisuals(XmlElement link)
        {
            var result = new List<UrdfVisual>();
            foreach (XmlElement vis in link.GetElementsByTagName("visual")
                         .OfType<XmlElement>())
            {
                var v = new UrdfVisual();
                var origin = vis.GetElementsByTagName("origin")
                    .OfType<XmlElement>().FirstOrDefault();
                if (origin != null)
                {
                    v.xyz = ParseVec3(origin.GetAttribute("xyz"), Vector3.zero);
                    v.rpy = ParseVec3(origin.GetAttribute("rpy"), Vector3.zero);
                }
                var mesh = vis.GetElementsByTagName("mesh")
                    .OfType<XmlElement>().FirstOrDefault();
                if (mesh != null)
                {
                    v.mesh = mesh.GetAttribute("filename")
                        .Replace("package://", "").TrimStart('/');
                    // Staged layout: URDF sits next to meshes/, plain relative paths.
                    int slash = v.mesh.IndexOf("meshes/", System.StringComparison.Ordinal);
                    if (slash > 0) v.mesh = v.mesh.Substring(slash);
                    if (mesh.HasAttribute("scale"))
                        v.scale = ParseVec3(mesh.GetAttribute("scale"), Vector3.one);
                }
                else
                {
                    var box = vis.GetElementsByTagName("box")
                        .OfType<XmlElement>().FirstOrDefault();
                    var cyl = vis.GetElementsByTagName("cylinder")
                        .OfType<XmlElement>().FirstOrDefault();
                    var sph = vis.GetElementsByTagName("sphere")
                        .OfType<XmlElement>().FirstOrDefault();
                    if (box != null)
                    {
                        v.primitive = "box";
                        v.primSize = ParseVec3(box.GetAttribute("size"), Vector3.one);
                    }
                    else if (cyl != null)
                    {
                        v.primitive = "cylinder";
                        v.primSize = new Vector3(ParseFloat(cyl.GetAttribute("radius"), 0.05f),
                                                 ParseFloat(cyl.GetAttribute("length"), 0.1f), 0);
                    }
                    else if (sph != null)
                    {
                        v.primitive = "sphere";
                        v.primSize = new Vector3(ParseFloat(sph.GetAttribute("radius"), 0.05f), 0, 0);
                    }
                    else continue;    // no geometry
                }
                result.Add(v);
            }
            return result;
        }

        static UrdfJoint ParseJoint(XmlElement el)
        {
            var j = new UrdfJoint
            {
                name = el.GetAttribute("name"),
                type = el.GetAttribute("type"),
            };
            foreach (XmlElement c in el.ChildNodes.OfType<XmlElement>())
            {
                switch (c.Name)
                {
                    case "parent": j.parent = c.GetAttribute("link"); break;
                    case "child": j.child = c.GetAttribute("link"); break;
                    case "origin":
                        j.xyz = ParseVec3(c.GetAttribute("xyz"), Vector3.zero);
                        j.rpy = ParseVec3(c.GetAttribute("rpy"), Vector3.zero);
                        break;
                    case "axis":
                        j.axis = ParseVec3(c.GetAttribute("xyz"), new Vector3(1, 0, 0));
                        break;
                    case "mimic":
                        j.mimicJoint = c.GetAttribute("joint");
                        j.mimicMult = ParseFloat(c.GetAttribute("multiplier"), 1f);
                        j.mimicOff = ParseFloat(c.GetAttribute("offset"), 0f);
                        break;
                }
            }
            return j;
        }

        static Vector3 ParseVec3(string s, Vector3 fallback)
        {
            if (string.IsNullOrWhiteSpace(s)) return fallback;
            var parts = s.Split((char[])null,
                                System.StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length != 3) return fallback;
            return new Vector3(ParseFloat(parts[0], 0f), ParseFloat(parts[1], 0f),
                               ParseFloat(parts[2], 0f));
        }

        static float ParseFloat(string s, float fallback) =>
            float.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture,
                           out float f) ? f : fallback;

        // ---- ghost material --------------------------------------------------- //

        static Material GetOrCreateGhostMaterial()
        {
            var mat = AssetDatabase.LoadAssetAtPath<Material>(GhostMatPath);
            if (mat != null) return mat;
            // Built-in RP (this project has no SRP asset): Standard, Fade mode.
            mat = new Material(Shader.Find("Standard"));
            mat.SetFloat("_Mode", 2f);
            mat.SetOverrideTag("RenderType", "Transparent");
            mat.SetInt("_SrcBlend", (int)UnityEngine.Rendering.BlendMode.SrcAlpha);
            mat.SetInt("_DstBlend", (int)UnityEngine.Rendering.BlendMode.OneMinusSrcAlpha);
            mat.SetInt("_ZWrite", 0);
            mat.DisableKeyword("_ALPHATEST_ON");
            mat.EnableKeyword("_ALPHABLEND_ON");
            mat.DisableKeyword("_ALPHAPREMULTIPLY_ON");
            mat.renderQueue = (int)UnityEngine.Rendering.RenderQueue.Transparent;
            mat.color = new Color(0.35f, 0.85f, 1.0f, 0.35f);   // hologram cyan
            mat.SetFloat("_Glossiness", 0.2f);
            AssetDatabase.CreateAsset(mat, GhostMatPath);
            return mat;
        }
    }
}
