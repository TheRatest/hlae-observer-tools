using System;
using System.Collections.Generic;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.OpenGL;
using Avalonia.OpenGL.Controls;
using Avalonia.Threading;
using OpenTK.Graphics.OpenGL4;
using OpenTK.Mathematics;
using HlaeObsTools.Services.Viewport3D;

namespace HlaeObsTools.Controls;

public sealed class OpenTkViewport : OpenGlControlBase
{
    public static readonly StyledProperty<string?> MapPathProperty =
        AvaloniaProperty.Register<OpenTkViewport, string?>(nameof(MapPath));
    public static readonly StyledProperty<string?> StatusTextProperty =
        AvaloniaProperty.Register<OpenTkViewport, string?>(nameof(StatusText), string.Empty);

    private int _vao;
    private int _vbo;
    private int _shaderProgram;
    private int _vertexCount;
    private int _mvpLocation;
    private int _colorLocation;
    private int _lightDirLocation;
    private int _ambientLocation;
    private bool _glReady;
    private bool _supportsVao;
    private ObjMesh? _pendingMesh;
    private bool _meshDirty;
    private int _gridVao;
    private int _gridVbo;
    private int _gridVertexCount;
    private int _groundVao;
    private int _groundVbo;
    private int _groundVertexCount;
    private int _debugVao;
    private int _debugVbo;
    private int _debugVertexCount;
    private string _statusPrefix = string.Empty;
    private bool _showDebugTriangle = true;
    private bool _showGroundPlane = true;
    private string _inputStatus = "Input: idle";
    private readonly Vector3 _lightDir = Vector3.Normalize(new Vector3(0.4f, 0.9f, 0.2f));
    private const float AmbientLight = 0.25f;

    private Vector3 _target = Vector3.Zero;
    private float _distance = 10f;
    private float _yaw = MathHelper.DegreesToRadians(45f);
    private float _pitch = MathHelper.DegreesToRadians(30f);
    private float _minDistance = 0.5f;
    private float _maxDistance = 1000f;

    private bool _dragging;
    private bool _panning;
    private Point _lastPointer;

    public OpenTkViewport()
    {
        Focusable = true;
        IsHitTestVisible = true;
        StatusText = "GL init pending...";
    }

    static OpenTkViewport()
    {
        MapPathProperty.Changed.AddClassHandler<OpenTkViewport>((sender, args) => sender.OnMapPathChanged(args));
    }

    public string? MapPath
    {
        get => GetValue(MapPathProperty);
        set => SetValue(MapPathProperty, value);
    }

    public string? StatusText
    {
        get => GetValue(StatusTextProperty);
        set => SetValue(StatusTextProperty, value);
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        RequestNextFrameRendering();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        _dragging = false;
        _panning = false;
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        HandlePointerPressed(e);
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        HandlePointerReleased(e);
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        HandlePointerMoved(e);
    }

    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        base.OnPointerWheelChanged(e);
        HandlePointerWheel(e);
    }

    public void ForwardPointerPressed(PointerPressedEventArgs e)
    {
        HandlePointerPressed(e);
    }

    public void ForwardPointerReleased(PointerReleasedEventArgs e)
    {
        HandlePointerReleased(e);
    }

    public void ForwardPointerMoved(PointerEventArgs e)
    {
        HandlePointerMoved(e);
    }

    public void ForwardPointerWheel(PointerWheelEventArgs e)
    {
        HandlePointerWheel(e);
    }

    private void HandlePointerPressed(PointerPressedEventArgs e)
    {
        var point = e.GetCurrentPoint(this);
        var updateKind = point.Properties.PointerUpdateKind;
        var middlePressed = point.Properties.IsMiddleButtonPressed || updateKind == PointerUpdateKind.MiddleButtonPressed;

        UpdateInputStatus($"Input: down M:{middlePressed} Shift:{e.KeyModifiers.HasFlag(KeyModifiers.Shift)}");

        if (!middlePressed)
            return;

        _dragging = true;
        _panning = e.KeyModifiers.HasFlag(KeyModifiers.Shift);
        _lastPointer = point.Position;
        e.Pointer.Capture(this);
        Focus();
        e.Handled = true;
    }

    private void HandlePointerReleased(PointerReleasedEventArgs e)
    {
        if (!_dragging)
            return;

        var point = e.GetCurrentPoint(this);
        var updateKind = point.Properties.PointerUpdateKind;
        var middlePressed = point.Properties.IsMiddleButtonPressed;
        var released = updateKind == PointerUpdateKind.MiddleButtonReleased
            || (!middlePressed);

        if (released)
        {
            _dragging = false;
            _panning = false;
            e.Pointer.Capture(null);
            UpdateInputStatus("Input: up");
        }
    }

    private void HandlePointerMoved(PointerEventArgs e)
    {
        if (!_dragging)
        {
            UpdateInputStatus("Input: move");
            return;
        }

        var point = e.GetCurrentPoint(this);
        var pos = point.Position;
        var delta = pos - _lastPointer;
        _lastPointer = pos;

        if (_panning)
        {
            Pan((float)delta.X, (float)delta.Y);
        }
        else
        {
            Orbit((float)delta.X, (float)delta.Y);
        }

        UpdateInputStatus("Input: drag");
        RequestNextFrameRendering();
        e.Handled = true;
    }

    private void HandlePointerWheel(PointerWheelEventArgs e)
    {
        if (Math.Abs(e.Delta.Y) < double.Epsilon)
            return;

        var zoomFactor = MathF.Pow(1.1f, (float)-e.Delta.Y);
        _distance = Math.Clamp(_distance * zoomFactor, _minDistance, _maxDistance);
        UpdateInputStatus($"Input: wheel {e.Delta.Y:0.##}");
        RequestNextFrameRendering();
        e.Handled = true;
    }

    protected override void OnOpenGlInit(GlInterface gl)
    {
        base.OnOpenGlInit(gl);

        GL.LoadBindings(new AvaloniaBindingsContext(gl));
        _shaderProgram = CreateShaderProgram();
        _mvpLocation = _shaderProgram == 0 ? -1 : GL.GetUniformLocation(_shaderProgram, "uMvp");
        _colorLocation = _shaderProgram == 0 ? -1 : GL.GetUniformLocation(_shaderProgram, "uColor");
        _lightDirLocation = _shaderProgram == 0 ? -1 : GL.GetUniformLocation(_shaderProgram, "uLightDir");
        _ambientLocation = _shaderProgram == 0 ? -1 : GL.GetUniformLocation(_shaderProgram, "uAmbient");
        _glReady = true;
        _supportsVao = CheckVaoSupport();

        GL.Enable(EnableCap.DepthTest);
        GL.LineWidth(1.5f);
        UpdateGrid(10f, 20);
        CreateGroundPlane(10f);
        UpdateStatusText();
    }

    protected override void OnOpenGlDeinit(GlInterface gl)
    {
        base.OnOpenGlDeinit(gl);
        _glReady = false;

        if (_vao != 0)
            GL.DeleteVertexArray(_vao);
        if (_vbo != 0)
            GL.DeleteBuffer(_vbo);
        if (_gridVao != 0)
            GL.DeleteVertexArray(_gridVao);
        if (_gridVbo != 0)
            GL.DeleteBuffer(_gridVbo);
        if (_groundVao != 0)
            GL.DeleteVertexArray(_groundVao);
        if (_groundVbo != 0)
            GL.DeleteBuffer(_groundVbo);
        if (_debugVao != 0)
            GL.DeleteVertexArray(_debugVao);
        if (_debugVbo != 0)
            GL.DeleteBuffer(_debugVbo);
        if (_shaderProgram != 0)
            GL.DeleteProgram(_shaderProgram);

        _vao = 0;
        _vbo = 0;
        _gridVao = 0;
        _gridVbo = 0;
        _groundVao = 0;
        _groundVbo = 0;
        _debugVao = 0;
        _debugVbo = 0;
        _shaderProgram = 0;
    }

    protected override void OnOpenGlRender(GlInterface gl, int fb)
    {
        if (!_glReady)
            return;

        if (_meshDirty)
            UploadPendingMesh();

        var width = Math.Max(1, (int)Bounds.Width);
        var height = Math.Max(1, (int)Bounds.Height);
        GL.Viewport(0, 0, width, height);

        GL.ClearColor(0.02f, 0.02f, 0.03f, 1f);
        GL.Clear(ClearBufferMask.ColorBufferBit | ClearBufferMask.DepthBufferBit);

        if (_showDebugTriangle && _debugVertexCount > 0 && _debugVbo != 0 && _shaderProgram != 0)
        {
            var mvp = Matrix4.Identity;
            ApplyCommonUniforms(ref mvp, new Vector3(1.0f, 0.2f, 0.8f));

            GL.Disable(EnableCap.DepthTest);
            BindGeometry(_debugVao, _debugVbo);
            GL.DrawArrays(PrimitiveType.Triangles, 0, _debugVertexCount);
            UnbindGeometry();
            GL.Enable(EnableCap.DepthTest);
        }

        if (_showGroundPlane && _groundVertexCount > 0 && _groundVbo != 0 && _shaderProgram != 0)
        {
            var mvp = CreateViewProjection(width, height);
            ApplyCommonUniforms(ref mvp, new Vector3(0.12f, 0.14f, 0.16f));

            BindGeometry(_groundVao, _groundVbo);
            GL.DrawArrays(PrimitiveType.Triangles, 0, _groundVertexCount);
            UnbindGeometry();
        }

        if (_gridVertexCount > 0 && _gridVbo != 0 && _shaderProgram != 0)
        {
            var mvp = CreateViewProjection(width, height);
            ApplyCommonUniforms(ref mvp, new Vector3(0.35f, 0.5f, 0.35f));

            GL.Disable(EnableCap.DepthTest);
            BindGeometry(_gridVao, _gridVbo);
            GL.DrawArrays(PrimitiveType.Lines, 0, _gridVertexCount);
            UnbindGeometry();
            GL.Enable(EnableCap.DepthTest);
        }

        if (_vertexCount > 0 && _vbo != 0 && _shaderProgram != 0)
        {
            var mvp = CreateViewProjection(width, height);
            ApplyCommonUniforms(ref mvp, new Vector3(0.82f, 0.86f, 0.9f));

            BindGeometry(_vao, _vbo);
            GL.DrawArrays(PrimitiveType.Triangles, 0, _vertexCount);
            UnbindGeometry();
        }

        RequestNextFrameRendering();
    }

    private void OnMapPathChanged(AvaloniaPropertyChangedEventArgs e)
    {
        var path = e.NewValue as string;
        if (string.IsNullOrWhiteSpace(path))
        {
            _pendingMesh = null;
            _meshDirty = true;
            RequestNextFrameRendering();
            return;
        }

        if (ObjMeshLoader.TryLoad(path, out var mesh, out _))
        {
            _pendingMesh = mesh;
            _meshDirty = true;
            RequestNextFrameRendering();
            return;
        }

        _pendingMesh = null;
        _meshDirty = true;
        RequestNextFrameRendering();
    }

    private void UploadPendingMesh()
    {
        _meshDirty = false;

        if (_pendingMesh == null)
        {
            if (_vao != 0)
                GL.DeleteVertexArray(_vao);
            if (_vbo != 0)
                GL.DeleteBuffer(_vbo);

            _vao = 0;
            _vbo = 0;
            _vertexCount = 0;
            return;
        }

        if (_vao != 0)
            GL.DeleteVertexArray(_vao);
        if (_vbo != 0)
            GL.DeleteBuffer(_vbo);

        _vao = _supportsVao ? GL.GenVertexArray() : 0;
        _vbo = GL.GenBuffer();

        if (_supportsVao)
            GL.BindVertexArray(_vao);
        GL.BindBuffer(BufferTarget.ArrayBuffer, _vbo);
        GL.BufferData(BufferTarget.ArrayBuffer, _pendingMesh.Vertices.Length * sizeof(float), _pendingMesh.Vertices, BufferUsageHint.StaticDraw);

        GL.EnableVertexAttribArray(0);
        GL.VertexAttribPointer(0, 3, VertexAttribPointerType.Float, false, 6 * sizeof(float), 0);
        GL.EnableVertexAttribArray(1);
        GL.VertexAttribPointer(1, 3, VertexAttribPointerType.Float, false, 6 * sizeof(float), 3 * sizeof(float));

        if (_supportsVao)
            GL.BindVertexArray(0);

        _vertexCount = _pendingMesh.VertexCount;
        ResetCameraToBounds(_pendingMesh.Min, _pendingMesh.Max);
        UpdateGridFromBounds(_pendingMesh.Min, _pendingMesh.Max);
        _pendingMesh = null;
    }

    private void SetStatusText(string? text)
    {
        if (Dispatcher.UIThread.CheckAccess())
        {
            StatusText = text;
        }
        else
        {
            Dispatcher.UIThread.Post(() => StatusText = text);
        }
    }

    private void UpdateStatusText()
    {
        var gridInfo = _gridVertexCount > 0 ? $"Grid verts: {_gridVertexCount}" : "Grid: none";
        var groundInfo = _groundVertexCount > 0 ? $"Ground verts: {_groundVertexCount}" : "Ground: none";
        var debugInfo = _showDebugTriangle ? "Debug: on" : "Debug: off";
        var prefix = string.IsNullOrWhiteSpace(_statusPrefix) ? "GL ready" : _statusPrefix;
        SetStatusText($"{prefix} | {gridInfo} | {groundInfo} | {debugInfo} | {_inputStatus}");
    }

    private void UpdateInputStatus(string status)
    {
        _inputStatus = status;
        UpdateStatusText();
    }

    private void ResetCameraToBounds(Vector3 min, Vector3 max)
    {
        _target = (min + max) * 0.5f;
        var radius = (max - min).Length * 0.5f;
        if (radius < 0.1f)
            radius = 0.1f;

        _distance = radius * 2.0f;
        _minDistance = radius * 0.2f;
        _maxDistance = radius * 20f;

        if (_distance < _minDistance)
            _distance = _minDistance;
        if (_distance > _maxDistance)
            _distance = _maxDistance;

        _yaw = MathHelper.DegreesToRadians(45f);
        _pitch = MathHelper.DegreesToRadians(30f);
    }

    private void UpdateGridFromBounds(Vector3 min, Vector3 max)
    {
        var extent = max - min;
        var maxExtent = MathF.Max(MathF.Max(extent.X, extent.Y), extent.Z);
        var size = MathF.Max(2f, maxExtent * 1.2f);
        UpdateGrid(size, 20);
    }

    private void UpdateGrid(float size, int divisions)
    {
        if (!_glReady)
            return;

        var half = size * 0.5f;
        var lines = divisions + 1;
        var vertices = new float[lines * 4 * 6];
        var step = size / divisions;
        var index = 0;

        for (var i = 0; i < lines; i++)
        {
            var offset = -half + i * step;

            AddVertex(vertices, ref index, -half, 0f, offset, 0f, 1f, 0f);
            AddVertex(vertices, ref index, half, 0f, offset, 0f, 1f, 0f);

            AddVertex(vertices, ref index, offset, 0f, -half, 0f, 1f, 0f);
            AddVertex(vertices, ref index, offset, 0f, half, 0f, 1f, 0f);
        }

        if (_gridVao != 0)
            GL.DeleteVertexArray(_gridVao);
        if (_gridVbo != 0)
            GL.DeleteBuffer(_gridVbo);

        _gridVao = _supportsVao ? GL.GenVertexArray() : 0;
        _gridVbo = GL.GenBuffer();

        if (_supportsVao)
            GL.BindVertexArray(_gridVao);
        GL.BindBuffer(BufferTarget.ArrayBuffer, _gridVbo);
        GL.BufferData(BufferTarget.ArrayBuffer, vertices.Length * sizeof(float), vertices, BufferUsageHint.StaticDraw);

        GL.EnableVertexAttribArray(0);
        GL.VertexAttribPointer(0, 3, VertexAttribPointerType.Float, false, 6 * sizeof(float), 0);
        GL.EnableVertexAttribArray(1);
        GL.VertexAttribPointer(1, 3, VertexAttribPointerType.Float, false, 6 * sizeof(float), 3 * sizeof(float));

        if (_supportsVao)
            GL.BindVertexArray(0);

        _gridVertexCount = vertices.Length / 6;
        RequestNextFrameRendering();
        UpdateStatusText();
    }

    private Vector3 GetCameraPosition()
    {
        var cosPitch = MathF.Cos(_pitch);
        var sinPitch = MathF.Sin(_pitch);
        var cosYaw = MathF.Cos(_yaw);
        var sinYaw = MathF.Sin(_yaw);

        var direction = new Vector3(cosPitch * cosYaw, sinPitch, cosPitch * sinYaw);
        return _target + direction * _distance;
    }

    private Matrix4 CreateViewProjection(int width, int height)
    {
        var aspect = width / (float)height;
        var nearPlane = Math.Max(0.05f, _distance * 0.01f);
        var farPlane = Math.Max(100f, _distance * 10f);
        var projection = Matrix4.CreatePerspectiveFieldOfView(MathHelper.DegreesToRadians(60f), aspect, nearPlane, farPlane);
        var view = Matrix4.LookAt(GetCameraPosition(), _target, Vector3.UnitY);
        return view * projection;
    }

    private void ApplyCommonUniforms(ref Matrix4 mvp, Vector3 color)
    {
        GL.UseProgram(_shaderProgram);
        if (_mvpLocation >= 0)
            GL.UniformMatrix4(_mvpLocation, false, ref mvp);
        if (_colorLocation >= 0)
            GL.Uniform3(_colorLocation, color);
        if (_lightDirLocation >= 0)
            GL.Uniform3(_lightDirLocation, _lightDir);
        if (_ambientLocation >= 0)
            GL.Uniform1(_ambientLocation, AmbientLight);
    }

    private void Orbit(float deltaX, float deltaY)
    {
        const float rotateSpeed = 0.01f;
        _yaw += deltaX * rotateSpeed;
        _pitch += deltaY * rotateSpeed;
        _pitch = Math.Clamp(_pitch, -1.55f, 1.55f);
    }

    private void Pan(float deltaX, float deltaY)
    {
        var cameraPos = GetCameraPosition();
        var forward = Vector3.Normalize(_target - cameraPos);
        var right = Vector3.Normalize(Vector3.Cross(forward, Vector3.UnitY));
        var up = Vector3.Normalize(Vector3.Cross(right, forward));

        var panScale = _distance * 0.001f;
        _target += (-right * deltaX + up * deltaY) * panScale;
    }

    private int CreateShaderProgram()
    {
        var version = GL.GetString(StringName.Version) ?? "unknown";
        var glsl = GL.GetString(StringName.ShadingLanguageVersion) ?? "unknown";
        var isEs = version.Contains("OpenGL ES", StringComparison.OrdinalIgnoreCase);
        var errors = new List<string>();

        var esVariants = new[]
        {
            new ShaderVariant("es300", VertexEs300, FragmentEs300, BindAttribLocation: false),
            new ShaderVariant("es100", VertexEs100, FragmentEs100, BindAttribLocation: true)
        };
        var desktopVariants = new[]
        {
            new ShaderVariant("gl330", Vertex330, Fragment330, BindAttribLocation: false),
            new ShaderVariant("gl150", Vertex150, Fragment150, BindAttribLocation: false),
            new ShaderVariant("gl120", Vertex120, Fragment120, BindAttribLocation: true)
        };

        var variants = new List<ShaderVariant>();
        if (isEs)
        {
            variants.AddRange(esVariants);
            variants.AddRange(desktopVariants);
        }
        else
        {
            variants.AddRange(desktopVariants);
            variants.AddRange(esVariants);
        }

        foreach (var variant in variants)
        {
            var vertexShader = CompileShader(ShaderType.VertexShader, variant.VertexSource, out var vertexError);
            if (vertexShader == 0)
            {
                if (!string.IsNullOrWhiteSpace(vertexError))
                    errors.Add($"Vertex {variant.Name}: {vertexError}");
                continue;
            }

            var fragmentShader = CompileShader(ShaderType.FragmentShader, variant.FragmentSource, out var fragmentError);
            if (fragmentShader == 0)
            {
                if (!string.IsNullOrWhiteSpace(fragmentError))
                    errors.Add($"Fragment {variant.Name}: {fragmentError}");
                GL.DeleteShader(vertexShader);
                continue;
            }

            var program = GL.CreateProgram();
            GL.AttachShader(program, vertexShader);
            GL.AttachShader(program, fragmentShader);
            if (variant.BindAttribLocation)
            {
                GL.BindAttribLocation(program, 0, "aPos");
                GL.BindAttribLocation(program, 1, "aNormal");
            }

            GL.LinkProgram(program);

            GL.GetProgram(program, GetProgramParameterName.LinkStatus, out var linked);
            if (linked == 0)
            {
                var info = GL.GetProgramInfoLog(program);
                if (!string.IsNullOrWhiteSpace(info))
                    errors.Add($"Link {variant.Name}: {info}");
                GL.DeleteProgram(program);
                program = 0;
            }

            if (program != 0)
            {
                GL.DetachShader(program, vertexShader);
                GL.DetachShader(program, fragmentShader);
            }
            GL.DeleteShader(vertexShader);
            GL.DeleteShader(fragmentShader);

            if (program != 0)
            {
                _statusPrefix = $"GL: {version} | GLSL: {glsl} | Shader: {variant.Name}";
                return program;
            }
        }

        _statusPrefix = errors.Count > 0
            ? $"Shader compile failed ({version}). {string.Join(" | ", errors)}"
            : $"Shader compile failed ({version}).";

        return 0;
    }

    private int CompileShader(ShaderType type, string source, out string? error)
    {
        error = null;
        var shader = GL.CreateShader(type);
        GL.ShaderSource(shader, source);
        GL.CompileShader(shader);

        GL.GetShader(shader, ShaderParameter.CompileStatus, out var status);
        if (status == 0)
        {
            error = GL.GetShaderInfoLog(shader);
            GL.DeleteShader(shader);
            return 0;
        }

        return shader;
    }

    private bool CheckVaoSupport()
    {
        var version = GL.GetString(StringName.Version) ?? string.Empty;
        var extensions = GL.GetString(StringName.Extensions) ?? string.Empty;
        if (version.Contains("OpenGL ES", StringComparison.OrdinalIgnoreCase))
            return version.Contains("OpenGL ES 3", StringComparison.OrdinalIgnoreCase) || extensions.Contains("GL_OES_vertex_array_object", StringComparison.OrdinalIgnoreCase);

        return true;
    }

    private void BindGeometry(int vao, int vbo)
    {
        if (_supportsVao && vao != 0)
        {
            GL.BindVertexArray(vao);
            return;
        }

        GL.BindBuffer(BufferTarget.ArrayBuffer, vbo);
        GL.EnableVertexAttribArray(0);
        GL.VertexAttribPointer(0, 3, VertexAttribPointerType.Float, false, 6 * sizeof(float), 0);
        GL.EnableVertexAttribArray(1);
        GL.VertexAttribPointer(1, 3, VertexAttribPointerType.Float, false, 6 * sizeof(float), 3 * sizeof(float));
    }

    private void UnbindGeometry()
    {
        if (_supportsVao)
        {
            GL.BindVertexArray(0);
            return;
        }

        GL.DisableVertexAttribArray(0);
        GL.DisableVertexAttribArray(1);
        GL.BindBuffer(BufferTarget.ArrayBuffer, 0);
    }

    private static void AddVertex(float[] buffer, ref int index, float x, float y, float z, float nx, float ny, float nz)
    {
        buffer[index++] = x;
        buffer[index++] = y;
        buffer[index++] = z;
        buffer[index++] = nx;
        buffer[index++] = ny;
        buffer[index++] = nz;
    }

    private void CreateGroundPlane(float size)
    {
        if (!_glReady)
            return;

        var half = size * 0.5f;
        var vertices = new[]
        {
            -half, 0f, -half, 0f, 1f, 0f,
            half, 0f, -half, 0f, 1f, 0f,
            half, 0f, half, 0f, 1f, 0f,

            -half, 0f, -half, 0f, 1f, 0f,
            half, 0f, half, 0f, 1f, 0f,
            -half, 0f, half, 0f, 1f, 0f
        };

        if (_groundVao != 0)
            GL.DeleteVertexArray(_groundVao);
        if (_groundVbo != 0)
            GL.DeleteBuffer(_groundVbo);

        _groundVao = _supportsVao ? GL.GenVertexArray() : 0;
        _groundVbo = GL.GenBuffer();

        if (_supportsVao)
            GL.BindVertexArray(_groundVao);
        GL.BindBuffer(BufferTarget.ArrayBuffer, _groundVbo);
        GL.BufferData(BufferTarget.ArrayBuffer, vertices.Length * sizeof(float), vertices, BufferUsageHint.StaticDraw);

        GL.EnableVertexAttribArray(0);
        GL.VertexAttribPointer(0, 3, VertexAttribPointerType.Float, false, 6 * sizeof(float), 0);
        GL.EnableVertexAttribArray(1);
        GL.VertexAttribPointer(1, 3, VertexAttribPointerType.Float, false, 6 * sizeof(float), 3 * sizeof(float));

        if (_supportsVao)
            GL.BindVertexArray(0);

        _groundVertexCount = vertices.Length / 6;
        RequestNextFrameRendering();
        UpdateStatusText();
    }

    private void CreateDebugTriangle()
    {
        if (!_glReady)
            return;

        var vertices = new[]
        {
            -0.8f, -0.8f, 0f, 0f, 0f, 1f,
            0.8f, -0.8f, 0f, 0f, 0f, 1f,
            0.0f, 0.8f, 0f, 0f, 0f, 1f
        };

        if (_debugVao != 0)
            GL.DeleteVertexArray(_debugVao);
        if (_debugVbo != 0)
            GL.DeleteBuffer(_debugVbo);

        _debugVao = _supportsVao ? GL.GenVertexArray() : 0;
        _debugVbo = GL.GenBuffer();

        if (_supportsVao)
            GL.BindVertexArray(_debugVao);
        GL.BindBuffer(BufferTarget.ArrayBuffer, _debugVbo);
        GL.BufferData(BufferTarget.ArrayBuffer, vertices.Length * sizeof(float), vertices, BufferUsageHint.StaticDraw);

        GL.EnableVertexAttribArray(0);
        GL.VertexAttribPointer(0, 3, VertexAttribPointerType.Float, false, 6 * sizeof(float), 0);
        GL.EnableVertexAttribArray(1);
        GL.VertexAttribPointer(1, 3, VertexAttribPointerType.Float, false, 6 * sizeof(float), 3 * sizeof(float));

        if (_supportsVao)
            GL.BindVertexArray(0);

        _debugVertexCount = vertices.Length / 6;
        RequestNextFrameRendering();
    }

    private readonly record struct ShaderVariant(string Name, string VertexSource, string FragmentSource, bool BindAttribLocation);

    private const string Vertex330 = """
        #version 330 core
        layout(location = 0) in vec3 aPos;
        layout(location = 1) in vec3 aNormal;
        uniform mat4 uMvp;
        out vec3 vNormal;
        void main()
        {
            vNormal = aNormal;
            gl_Position = uMvp * vec4(aPos, 1.0);
        }
        """;

    private const string Fragment330 = """
        #version 330 core
        in vec3 vNormal;
        out vec4 FragColor;
        uniform vec3 uColor;
        uniform vec3 uLightDir;
        uniform float uAmbient;
        void main()
        {
            float ndl = max(dot(normalize(vNormal), normalize(uLightDir)), 0.0);
            vec3 lit = uColor * (uAmbient + (1.0 - uAmbient) * ndl);
            FragColor = vec4(lit, 1.0);
        }
        """;

    private const string Vertex150 = """
        #version 150
        in vec3 aPos;
        in vec3 aNormal;
        uniform mat4 uMvp;
        out vec3 vNormal;
        void main()
        {
            vNormal = aNormal;
            gl_Position = uMvp * vec4(aPos, 1.0);
        }
        """;

    private const string Fragment150 = """
        #version 150
        in vec3 vNormal;
        out vec4 FragColor;
        uniform vec3 uColor;
        uniform vec3 uLightDir;
        uniform float uAmbient;
        void main()
        {
            float ndl = max(dot(normalize(vNormal), normalize(uLightDir)), 0.0);
            vec3 lit = uColor * (uAmbient + (1.0 - uAmbient) * ndl);
            FragColor = vec4(lit, 1.0);
        }
        """;

    private const string Vertex120 = """
        #version 120
        attribute vec3 aPos;
        attribute vec3 aNormal;
        uniform mat4 uMvp;
        varying vec3 vNormal;
        void main()
        {
            vNormal = aNormal;
            gl_Position = uMvp * vec4(aPos, 1.0);
        }
        """;

    private const string Fragment120 = """
        #version 120
        uniform vec3 uColor;
        uniform vec3 uLightDir;
        uniform float uAmbient;
        varying vec3 vNormal;
        void main()
        {
            float ndl = max(dot(normalize(vNormal), normalize(uLightDir)), 0.0);
            vec3 lit = uColor * (uAmbient + (1.0 - uAmbient) * ndl);
            gl_FragColor = vec4(lit, 1.0);
        }
        """;

    private const string VertexEs300 = """
        #version 300 es
        precision mediump float;
        layout(location = 0) in vec3 aPos;
        layout(location = 1) in vec3 aNormal;
        uniform mat4 uMvp;
        out vec3 vNormal;
        void main()
        {
            vNormal = aNormal;
            gl_Position = uMvp * vec4(aPos, 1.0);
        }
        """;

    private const string FragmentEs300 = """
        #version 300 es
        precision mediump float;
        out vec4 FragColor;
        in vec3 vNormal;
        uniform vec3 uColor;
        uniform vec3 uLightDir;
        uniform float uAmbient;
        void main()
        {
            float ndl = max(dot(normalize(vNormal), normalize(uLightDir)), 0.0);
            vec3 lit = uColor * (uAmbient + (1.0 - uAmbient) * ndl);
            FragColor = vec4(lit, 1.0);
        }
        """;

    private const string VertexEs100 = """
        attribute vec3 aPos;
        attribute vec3 aNormal;
        uniform mat4 uMvp;
        varying vec3 vNormal;
        void main()
        {
            vNormal = aNormal;
            gl_Position = uMvp * vec4(aPos, 1.0);
        }
        """;

    private const string FragmentEs100 = """
        precision mediump float;
        uniform vec3 uColor;
        uniform vec3 uLightDir;
        uniform float uAmbient;
        varying vec3 vNormal;
        void main()
        {
            float ndl = max(dot(normalize(vNormal), normalize(uLightDir)), 0.0);
            vec3 lit = uColor * (uAmbient + (1.0 - uAmbient) * ndl);
            gl_FragColor = vec4(lit, 1.0);
        }
        """;

    private sealed class AvaloniaBindingsContext : OpenTK.IBindingsContext
    {
        private readonly GlInterface _gl;

        public AvaloniaBindingsContext(GlInterface gl)
        {
            _gl = gl;
        }

        public IntPtr GetProcAddress(string procName)
        {
            return _gl.GetProcAddress(procName);
        }
    }
}
