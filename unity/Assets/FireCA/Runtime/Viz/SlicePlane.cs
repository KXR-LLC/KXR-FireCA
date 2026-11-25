using UnityEngine;
using UnityEngine.Rendering;

namespace FireCA
{
    /// <summary>
    /// The slice plane: a vertical quad that cuts the Wind Grid and shows one field in color (docs/04-visualization.md).
    /// The user moves the slice plane along its axis and switches between an east-west cut and a north-south cut.
    /// </summary>
    public sealed class SlicePlane : MonoBehaviour
    {
        /// <summary>The identifiers match the field constants in SlicePlane.shader.</summary>
        public enum Field { WindSpeed = 0, VerticalVelocity = 1, Theta = 2, SmokeDensity = 3 }

        public static readonly string[] FieldNames = { "Wind speed", "Vertical velocity", "Theta", "Smoke density" };
        public static readonly string[] FieldRanges =
        {
            "0 to 20 m/s", "-5 m/s (blue) to 5 m/s (red)", "-5 K (blue) to 5 K (red)", "0 (clear) to 1 (dark)",
        };

        /// <summary>The height of the slice plane above the highest elevation, m.</summary>
        const float HeadroomM = 1600f;

        [SerializeField] Material sliceMaterial;
        public Material SliceMaterial { get => sliceMaterial; set => sliceMaterial = value; }

        public bool Visible { get; private set; }
        public Field SelectedField { get; private set; } = Field.WindSpeed;
        /// <summary>True: the plane runs east to west and moves along Z. False: the plane runs north to south and moves along X.</summary>
        public bool EastWestCut { get; private set; } = true;
        /// <summary>The position of the plane along its axis, from 0 to 1.</summary>
        public float Position01 { get; private set; } = 0.5f;

        FireCAApp _app;
        GameObject _quad;
        Mesh _mesh;
        Material _material;

        void Start()
        {
            _app = FireCAApp.Instance;
            if (_app != null) _app.ScenarioChanged += Rebuild;
            if (sliceMaterial != null) _material = new Material(sliceMaterial) { hideFlags = HideFlags.HideAndDontSave };
            _mesh = new Mesh { name = "Slice Plane", hideFlags = HideFlags.HideAndDontSave };
            _quad = new GameObject("Slice Plane");
            _quad.transform.SetParent(transform, false);
            _quad.AddComponent<MeshFilter>().sharedMesh = _mesh;
            var renderer = _quad.AddComponent<MeshRenderer>();
            renderer.sharedMaterial = _material;
            renderer.shadowCastingMode = ShadowCastingMode.Off;
            renderer.receiveShadows = false;
            Rebuild();
        }

        void OnDestroy()
        {
            if (_app != null) _app.ScenarioChanged -= Rebuild;
            if (_mesh != null) Destroy(_mesh);
            if (_material != null) Destroy(_material);
        }

        public void SetVisible(bool visible) { Visible = visible; Rebuild(); }
        public void SetField(Field field) { SelectedField = field; Rebuild(); }
        public void SetEastWestCut(bool eastWest) { EastWestCut = eastWest; Rebuild(); }
        public void SetPosition(float position01) { Position01 = Mathf.Clamp01(position01); Rebuild(); }

        void Rebuild()
        {
            if (_quad == null) return;
            bool show = Visible && _app != null && _app.Scenario != null && _app.Wind != null && _app.Wind.Enabled && _material != null;
            _quad.SetActive(show);
            if (!show) return;

            GridSpec grid = _app.Scenario.Grid;
            float extent = grid.ExtentM;
            float bottom = grid.WindBottomY;
            float top = Mathf.Min(bottom + GridSpec.WindCellsY * GridSpec.WindCellSizeM, _app.Scenario.MaxElevation + HeadroomM);
            float at = Mathf.Lerp(GridSpec.WindCellSizeM, extent - GridSpec.WindCellSizeM, Position01);
            Vector3 a, b;
            if (EastWestCut) { a = new Vector3(0f, 0f, at); b = new Vector3(extent, 0f, at); }
            else { a = new Vector3(at, 0f, 0f); b = new Vector3(at, 0f, extent); }

            _mesh.Clear();
            _mesh.SetVertices(new[]
            {
                new Vector3(a.x, bottom, a.z), new Vector3(b.x, bottom, b.z),
                new Vector3(b.x, top, b.z), new Vector3(a.x, top, a.z),
            });
            _mesh.SetIndices(new[] { 0, 1, 2, 0, 2, 3 }, MeshTopology.Triangles, 0);
            _mesh.RecalculateBounds();
            _material.SetFloat("_SliceField", (float)SelectedField);
        }
    }
}
