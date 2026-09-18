using System;
using System.Collections.Generic;
using UnityEngine;

namespace Manimal.MotionMatching
{
    // world-space wire boxes and lines, drawn with GL after the main camera renders. the buffer is filled during
    // the frame and cleared once drawn, so anything that wants to show up has to be queued every frame
    internal static class DebugDraw
    {
        private struct Segment { public Vector3 A, B; public Color Color; }

        private static readonly List<Segment> Segments = new List<Segment>();
        private static Material _material;
        private static bool _installed;

        public static bool Enabled;

        public static void Install()
        {
            if (_installed) return;
            Camera.onPostRender += OnPostRender;
            _installed = true;
        }

        public static void Uninstall()
        {
            if (!_installed) return;
            Camera.onPostRender -= OnPostRender;
            _installed = false;
            Segments.Clear();
        }

        public static void Line(Vector3 a, Vector3 b, Color color)
        {
            if (!Enabled) return;
            Segments.Add(new Segment { A = a, B = b, Color = color });
        }

        public static void Polyline(IList<Vector3> points, Color color)
        {
            if (!Enabled || points == null) return;
            for (int i = 1; i < points.Count; i++)
                Line(points[i - 1], points[i], color);
        }

        // axis-aligned wire box, footprint-sized by default so it reads like Valve's step markers
        public static void Box(Vector3 center, Color color, float length = 0.28f, float width = 0.12f, float height = 0.04f, float yawDegrees = 0f)
        {
            if (!Enabled) return;
            Quaternion yaw = Quaternion.Euler(0f, yawDegrees, 0f);
            Vector3 x = yaw * Vector3.right * (width * 0.5f), z = yaw * Vector3.forward * (length * 0.5f), y = Vector3.up * height;
            Vector3[] c = new Vector3[8];
            for (int i = 0; i < 8; i++)
                c[i] = center + ((i & 1) == 0 ? -x : x) + ((i & 2) == 0 ? -z : z) + ((i & 4) == 0 ? Vector3.zero : y);
            int[] edges = { 0, 1, 1, 3, 3, 2, 2, 0, 4, 5, 5, 7, 7, 6, 6, 4, 0, 4, 1, 5, 2, 6, 3, 7 };
            for (int i = 0; i < edges.Length; i += 2)
                Line(c[edges[i]], c[edges[i + 1]], color);
            // a tick along +z marks the heading
            Line(center + z + y * 0.5f, center + z * 1.4f + y * 0.5f, color);
        }

        private static void OnPostRender(Camera camera)
        {
            // a throwing callback here would silently kill every later onPostRender subscriber (see CLAUDE.md)
            try
            {
                if (Segments.Count == 0) return;
                // only the scene camera; the segment list is consumed by whichever main camera draws first
                if (camera != Camera.main) return;
                if (!_material)
                {
                    var shader = Shader.Find("Hidden/Internal-Colored");
                    if (!shader) { Segments.Clear(); return; }
                    _material = new Material(shader) { hideFlags = HideFlags.HideAndDontSave };
                    _material.SetInt("_ZWrite", 0);
                    _material.SetInt("_ZTest", (int)UnityEngine.Rendering.CompareFunction.Always);
                }
                _material.SetPass(0);
                GL.PushMatrix();
                GL.Begin(GL.LINES);
                for (int i = 0; i < Segments.Count; i++)
                {
                    GL.Color(Segments[i].Color);
                    GL.Vertex(Segments[i].A);
                    GL.Vertex(Segments[i].B);
                }
                GL.End();
                GL.PopMatrix();
                Segments.Clear();
            }
            catch (Exception ex)
            {
                Segments.Clear();
                Debug.LogError("[" + ModInfo.Name + "] debug draw: " + ex);
            }
        }
    }
}
