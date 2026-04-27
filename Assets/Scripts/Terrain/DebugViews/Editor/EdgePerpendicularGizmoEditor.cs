using UnityEditor;
using UnityEngine;

namespace Terrain.DebugViews.EditorOnly
{
    // Companion to EdgePerpendicularGizmo. Draws region-id labels at each end of the
    // perpendicular while the gizmo object is selected (Handles.Label is editor-only).
    [CustomEditor(typeof(EdgePerpendicularGizmo))]
    public class EdgePerpendicularGizmoEditor : Editor
    {
        private GUIStyle _style;

        private void OnSceneGUI()
        {
            var gizmo = (EdgePerpendicularGizmo)target;
            if (gizmo == null || !gizmo.show) return;

            _style ??= new GUIStyle(EditorStyles.boldLabel)
            {
                normal = { textColor = Color.white },
                alignment = TextAnchor.MiddleCenter,
            };

            foreach (var e in gizmo.CachedEntries)
            {
                if (e.posRegionId >= 0) Handles.Label(e.worldPosEnd, e.posRegionId.ToString(), _style);
                if (e.negRegionId >= 0) Handles.Label(e.worldNegEnd, e.negRegionId.ToString(), _style);
            }
        }
    }
}
