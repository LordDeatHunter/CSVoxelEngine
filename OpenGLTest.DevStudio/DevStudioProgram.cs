using System;
using System.Globalization;
using System.IO;
using System.Numerics;
using Silk.NET.Input;
using Silk.NET.Maths;
using Silk.NET.OpenGL;
using Silk.NET.Windowing;
using VoxelEngine.App;
using VoxelEngine.Content.Blocks;
using VoxelEngine.Items;
using VoxelEngine.Particles;
using VoxelEngine.Platform;
using VoxelEngine.Player;
using VoxelEngine.VisualTest;

namespace VoxelEngine.DevStudio;

/// <summary>
/// Standalone dev-studio entrypoint: a window showing the player model in its carrying pose
/// with the selected item in the right hand. Left-drag orbits the camera, right-drag moves the
/// model, middle-drag pans, the wheel zooms (or scrolls the item grid), and the UI edits the
/// item's display transform and previews the walk/mine/jump animations. Three tabs: ITEMS (the
/// above), ENTITIES, which swaps the player for a registered entity and previews each of its
/// Blockbench clips (and the hurt flash) through <see cref="EntityPreviewState"/>, and TREES,
/// which grows the selected tree species pulse by pulse over flat grass at a fixed seed through
/// <see cref="TreePreviewState"/>, framed on the tree's bounds (the wheel zooms that framing). QUESTS edits the
/// quest graphs under <c>assets/data/quests</c> (<see cref="QuestEditorPage"/>) and saves them back.
/// <c>DEV_STUDIO_SELECT</c> resolves an item, then an entity, then a species (a <c>tree/</c>
/// prefix forces the species, a <c>quest/</c> prefix selects a quest on the QUESTS tab), and
/// <c>DEV_STUDIO_TREE_STEP</c> (an integer or <c>mature</c>) pins the shown growth step for a scripted capture.
/// </summary>
public static class DevStudioProgram
{
    private static IWindow _window = null!;
    private static GL _gl = null!;
    private static InputManager _input = null!;
    private static DebugSceneRenderer _scene = null!;
    private static DevStudioUi _ui = null!;
    // No initializer: it snapshots EntityRegistry, so it is built in OnLoad once the scene
    // renderer has loaded the content; a static initializer would see an empty registry.
    private static EntityPreviewState _entities = null!;
    // No initializer for the same reason: it snapshots TreeSpeciesRegistry.
    private static TreePreviewState _trees = null!;
    // Built in OnLoad too: the quest editor compiles against the loaded blocks, items and entities.
    private static QuestEditorPage _quests = null!;
    private const string QuestPrefix = "quest/";
    private static StudioTab _tab;
    // The player model the ITEMS tab poses; the preview state replaces it on a restart, so it is read through here.
    private static PlayerModel Player => _playerAnim.Model;
    // The timeline of each animated tab: progress, speed and loop over the state's one play.
    private static readonly AnimationClock PlayerClock = new();
    private static readonly AnimationClock EntityClock = new();
    private static AnimationClock ActiveClock => _tab == StudioTab.Entities ? EntityClock : PlayerClock;
    // The player's animation modes (idle, walk, jump, swim, punch, slice, throw, ...), driving Player.
    private static PlayerPreviewState _playerAnim = null!;
    private static readonly ItemDisplayEditor Editor = new();

    private static float _orbitYaw = 30f;
    private static float _orbitPitch = 12f;
    private static float _distance = 4.5f;
    // The trees tab frames the tree's bounds itself; the wheel scales that fitted distance.
    private static float _treeZoom = 1f;
    private const float FieldOfViewY = 70f * MathF.PI / 180f;
    private const float NearPlane = 0.05f;
    private const float FarPlane = 200f;
    private static Vector3 _pan;
    private static Vector3 _modelOffset;
    private const float BodyYaw = -90f;
    // Orbit targets: the player's chest, and roughly the middle of a goblin-sized entity.
    private static readonly Vector3 PlayerOrbitTarget = new(0f, 0.95f, 0f);
    private static readonly Vector3 EntityOrbitTarget = new(0f, 0.65f, 0f);
    private static bool _firstPerson;
    private static float _lookYaw = BodyYaw;
    private static float _lookPitch;
    private static Vector2 _mouseDelta;
    private static Vector2 _lastMouse;
    private static bool _haveMouse;
    private static bool _captured;
    private static bool _leftDrag;
    private static bool _rightDrag;
    private static bool _middleDrag;
    private static string? _dragSlider;
    private static string _status = "";

    private static double _time;

    // Set to a PNG path to render a few frames, save one screenshot and quit. Used for
    // scripted verification of the tool without a human at the window.
    private const string ScreenshotVariable = "DEV_STUDIO_SHOT";
    private static string? _screenshotPath;
    // Seconds of simulated animation to run before the screenshot (DEV_STUDIO_SHOT_AT); null = the
    // usual early frame.
    private const string ShotAtVariable = "DEV_STUDIO_SHOT_AT";
    private static double? _shotAt;
    private static double _simulated;
    private static int _frameCount;
    private static bool _closing;

    public static void Main()
    {
        AssetRoot.UseAssetsDirectory();
        _screenshotPath = Environment.GetEnvironmentVariable(ScreenshotVariable)?.Trim();
        if (double.TryParse(Environment.GetEnvironmentVariable(ShotAtVariable)?.Trim(), NumberStyles.Float,
                CultureInfo.InvariantCulture, out var shotAt) && shotAt >= 0.0)
            _shotAt = shotAt;
        Log.WriteLine($"DevStudio assets: {Directory.GetCurrentDirectory()}");

        var options = WindowOptions.Default with
        {
            Title = "Dev Studio",
            Size = new Vector2D<int>(1600, 900),
            PreferredDepthBufferBits = 24,
            API = new GraphicsAPI(ContextAPI.OpenGL, ContextProfile.Core, ContextFlags.Default,
                new APIVersion(4, 6)),
            VSync = true,
        };

        _window = Window.Create(options);
        _window.Load += OnLoad;
        _window.Update += OnUpdate;
        _window.Render += OnRender;
        _window.Closing += OnClosing;
        _window.Run();
    }

    private static void OnLoad()
    {
        _gl = _window.CreateOpenGL();
        _gl.Enable(EnableCap.DepthTest);
        _gl.DepthFunc(DepthFunction.Lequal);

        _scene = new DebugSceneRenderer(_gl);
        _input = new InputManager(_window);
        _quests = new QuestEditorPage(QuestEditorState.Load(Path.Combine("assets", "data", "quests")),
            new TextFieldKeys(_input));
        _quests.Frame(new Vector2(_window.Size.X, _window.Size.Y));
        _entities = new EntityPreviewState();
        _trees = new TreePreviewState();
        _ui = new DevStudioUi();
        _playerAnim = new PlayerPreviewState(new PlayerModel { Rig = _scene.Rig, HoldingItem = true });
        _playerAnim.TrailSampler = SampleTrail;
        PlayerClock.Attach(_playerAnim);
        EntityClock.Attach(_entities);

        if (_ui.Entries.Count > 0)
            Editor.Select(_ui.Entries[0].Id);
        var select = Environment.GetEnvironmentVariable("DEV_STUDIO_SELECT")?.Trim();
        if (select?.StartsWith(QuestPrefix, StringComparison.OrdinalIgnoreCase) == true)
        {
            var id = select[QuestPrefix.Length..];
            if (_quests.State.Find(id) != null)
            {
                _tab = StudioTab.Quests;
                _quests.State.Select(id);
                _quests.Frame(new Vector2(_window.Size.X, _window.Size.Y), id);
            }
            else
                Log.Warn($"DEV_STUDIO_SELECT: no quest with id '{id}'.");
        }
        else if (!string.IsNullOrEmpty(select))
        {
            // Items, then entities, then tree species; "tree/<name>" skips straight to the species,
            // since a bare name such as "cactus" is also a block.
            var forceTree = select.StartsWith(TreePreviewState.NamePrefix, StringComparison.OrdinalIgnoreCase);
            if (!forceTree && TrySelectByName(select))
                _tab = StudioTab.Items;
            else if (!forceTree && _entities.SelectByName(select))
                _tab = StudioTab.Entities;
            else if (_trees.SelectByName(select))
                _tab = StudioTab.Trees;
            else
                Log.Warn($"DEV_STUDIO_SELECT: no item, entity or tree species named '{select}'.");
        }

        // The tree growth step to show: an integer (clamped to the species' range) or "mature".
        var treeStep = Environment.GetEnvironmentVariable("DEV_STUDIO_TREE_STEP")?.Trim();
        if (!string.IsNullOrEmpty(treeStep))
        {
            if (string.Equals(treeStep, "mature", StringComparison.OrdinalIgnoreCase))
                _trees.Mature();
            else if (int.TryParse(treeStep, NumberStyles.Integer, CultureInfo.InvariantCulture, out var step))
                _trees.SetStep(step);
            else
                Log.Warn($"DEV_STUDIO_TREE_STEP: '{treeStep}' is neither an integer step nor 'mature'.");
        }

        // A player animation (DEV_STUDIO_PLAYER_ANIM=slice, throw, swim, ...) selects the ITEMS tab.
        var playerAnim = Environment.GetEnvironmentVariable("DEV_STUDIO_PLAYER_ANIM")?.Trim();
        if (!string.IsNullOrEmpty(playerAnim))
        {
            if (_playerAnim.SetMode(playerAnim)) _tab = StudioTab.Items;
            else Log.Warn($"DEV_STUDIO_PLAYER_ANIM: '{playerAnim}' is not a player animation " +
                          $"({string.Join(", ", PlayerPreviewState.AllModes)}).");
        }

        // A clip name of the selected entity, or "hurt".
        var anim = Environment.GetEnvironmentVariable("DEV_STUDIO_ANIM")?.Trim();
        if (!string.IsNullOrEmpty(anim) && !_entities.SetMode(anim))
            Log.Warn($"DEV_STUDIO_ANIM: '{anim}' is not a mode of " +
                     $"{_entities.Selected?.DisplayName ?? "any entity"} ({string.Join(", ", _entities.Modes)}).");

        if (string.Equals(Environment.GetEnvironmentVariable("DEV_STUDIO_VIEW")?.Trim(), "first",
                StringComparison.OrdinalIgnoreCase))
            _firstPerson = true;

        // The orbit camera's yaw and pitch in degrees: yaw 0 looks at the player's back (the model faces -Z),
        // 90 from its right side... 180 from the front, which is where a limb coming off the body shows.
        var orbit = Environment.GetEnvironmentVariable("DEV_STUDIO_ORBIT")?.Trim();
        if (!string.IsNullOrEmpty(orbit))
        {
            var parts = orbit.Split(',');
            if (parts.Length == 2
                && float.TryParse(parts[0].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var oy)
                && float.TryParse(parts[1].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var op))
            {
                _orbitYaw = oy;
                _orbitPitch = Math.Clamp(op, -85f, 85f);
            }
            else
                Log.Warn($"DEV_STUDIO_ORBIT: '{orbit}' is not 'yaw,pitch' in degrees.");
        }

        var look = Environment.GetEnvironmentVariable("DEV_STUDIO_LOOK")?.Trim();
        if (!string.IsNullOrEmpty(look) && TryParseLook(look, out var lookYaw, out var lookPitch))
        {
            _lookYaw = lookYaw;
            _lookPitch = lookPitch;
        }

        // Playback options: DEV_STUDIO_SPEED=<0..4> and DEV_STUDIO_LOOP=<0|1> apply to both timelines.
        var speed = Environment.GetEnvironmentVariable("DEV_STUDIO_SPEED")?.Trim();
        if (!string.IsNullOrEmpty(speed))
        {
            if (float.TryParse(speed, NumberStyles.Float, CultureInfo.InvariantCulture, out var speedValue))
                PlayerClock.Speed = EntityClock.Speed = speedValue;
            else
                Log.Warn($"DEV_STUDIO_SPEED: '{speed}' is not a number.");
        }
        var loop = Environment.GetEnvironmentVariable("DEV_STUDIO_LOOP")?.Trim();
        if (loop is "0" or "1")
            PlayerClock.Loop = EntityClock.Loop = loop == "1";
        // The mode may have been picked above: start both timelines from its beginning.
        PlayerClock.Reset();
        EntityClock.Reset();

        if (_input.Mouse is { } mouse)
        {
            mouse.MouseDown += OnMouseDown;
            mouse.MouseUp += OnMouseUp;
            mouse.MouseMove += OnMouseMove;
            mouse.Scroll += OnScroll;
        }
        if (_input.Keyboard is { } keyboard)
        {
            keyboard.KeyDown += OnKeyDown;
            keyboard.KeyChar += (_, c) =>
            {
                if (_tab == StudioTab.Quests) _quests.KeyChar(c);
            };
        }
    }

    // The QUESTS tab takes its own keys; elsewhere Space pauses or resumes the animation timeline of the tab shown.
    private static void OnKeyDown(IKeyboard keyboard, Key key, int scancode)
    {
        if (_tab == StudioTab.Quests)
        {
            _quests.KeyDown(key, Held(Key.ControlLeft, Key.ControlRight));
            return;
        }
        if (key != Key.Space || _tab == StudioTab.Trees) return;

        ActiveClock.Paused = !ActiveClock.Paused;
    }

    private static bool TrySelectByName(string name)
    {
        foreach (var entry in _ui.Entries)
        {
            var matches = entry.Name.Equals(name, StringComparison.OrdinalIgnoreCase)
                || (ItemRegistry.TryGet(entry.Id) is { } item
                    && item.SourceName.Equals(name, StringComparison.OrdinalIgnoreCase));
            if (!matches) continue;

            Editor.Select(entry.Id);
            return true;
        }
        return false;
    }

    private static bool TryParseLook(string value, out float yaw, out float pitch)
    {
        yaw = _lookYaw;
        pitch = _lookPitch;
        var parts = value.Split(',');
        return parts.Length == 2
            && float.TryParse(parts[0].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out yaw)
            && float.TryParse(parts[1].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out pitch);
    }

    private static void OnUpdate(double deltaTime)
    {
        if (_closing)
        {
            _window.Close();
            return;
        }

        // With DEV_STUDIO_SHOT_AT the simulation steps a fixed 1/60 s a frame, so a scripted capture
        // lands on the same instant of an animation every run.
        var dt = _shotAt != null ? 1.0 / 60.0 : Math.Clamp(deltaTime, 0.0, 0.05);
        _time += _shotAt != null ? dt : deltaTime;
        _simulated += dt;

        var motion = _mouseDelta;
        _mouseDelta = Vector2.Zero;
        var mouse = _input?.Mouse;

        if (mouse != null)
        {
            if (_leftDrag && mouse.IsButtonPressed(MouseButton.Left))
            {
                if (_firstPerson && _tab == StudioTab.Items) Look(motion);
                else Orbit(motion);
            }

            if (_rightDrag && mouse.IsButtonPressed(MouseButton.Right))
                MoveModel(motion);

            if (_middleDrag && mouse.IsButtonPressed(MouseButton.Middle))
                Pan(motion);

            if (_dragSlider is { } label)
            {
                var slider = _ui.Slider(label);
                if (slider != null && mouse.IsButtonPressed(MouseButton.Left))
                    slider.Set(DevStudioUi.ValueAt(slider, mouse.Position.X));
                else
                    _dragSlider = null;
            }
        }

        // Dragging the progress slider scrubs: the clocks stop and the slider owns the time.
        PlayerClock.Scrubbing = EntityClock.Scrubbing = _dragSlider == DevStudioUi.ProgressSlider;

        switch (_tab)
        {
            case StudioTab.Entities:
                EntityClock.Update((float)dt);
                break;
            case StudioTab.Trees or StudioTab.Quests:
                // A tree only changes on a step, seed or species change, a quest graph on an edit; nothing animates.
                break;
            default:
                UpdateAnimation((float)dt);
                break;
        }
    }

    private static void OnRender(double deltaTime)
    {
        var size = _window.Size;
        if (size.X <= 0 || size.Y <= 0) return;

        var aspect = size.X / (float)size.Y;
        var projection = Matrix4x4.CreatePerspectiveFieldOfView(FieldOfViewY, aspect, NearPlane, FarPlane);
        switch (_tab)
        {
            case StudioTab.Entities:
            {
                // First person has no meaning without the player, so the entity tab always orbits.
                var entityView = BuildView(EntityOrbitTarget, out var cameraEye);
                _scene.RenderEntity(entityView * projection, cameraEye, _entities, (float)_time, size);
                break;
            }
            case StudioTab.Trees:
                RenderTreeScene(aspect, size);
                break;
            case StudioTab.Quests:
                // No scene: the quest canvas covers the window
                _gl.Viewport(0, 0, (uint)size.X, (uint)size.Y);
                _gl.Clear(ClearBufferMask.ColorBufferBit | ClearBufferMask.DepthBufferBit);
                break;
            default:
                RenderItemScene(projection, size);
                break;
        }

        _scene.Ui.Begin(size);
        if (_tab == StudioTab.Quests)
            _quests.DrawCanvas(_scene.Ui, _haveMouse ? _lastMouse : null, (float)_time);
        _ui.Draw(_scene.Ui, Editor, _entities, _trees, _tab, _playerAnim, new Vector2(size.X, size.Y), _status,
            _firstPerson && _tab == StudioTab.Items, _tab is StudioTab.Items or StudioTab.Entities ? ActiveClock : null,
            _quests.HighlightedIcon);
        if (_tab == StudioTab.Quests)
            _quests.DrawPanels(_scene.Ui, (float)_time);
        _scene.Ui.End();
        _gl.BindVertexArray(0);

        CaptureScreenshot(size);
    }

    // Orbits the tree's framing center at the distance that fits its framing sphere in view, so a
    // baobab and a cactus both fill the frame; the far plane grows with it so the crown never clips.
    private static void RenderTreeScene(float aspect, Vector2D<int> size)
    {
        var radius = _trees.FramingRadius;
        var distance = TreeViewDistance(aspect);
        var projection = Matrix4x4.CreatePerspectiveFieldOfView(FieldOfViewY, aspect, NearPlane,
            MathF.Max(FarPlane, distance + 2f * radius));
        var view = BuildView(_trees.FramingCenter, distance, out var eye);
        _scene.RenderTree(view * projection, eye, _trees, (float)_time, size);
    }

    private static float TreeViewDistance(float aspect) =>
        TreePreviewState.FitDistance(_trees.FramingRadius, FieldOfViewY, aspect) * _treeZoom;

    private static float WindowAspect()
    {
        var size = _window.Size;
        return size.X > 0 && size.Y > 0 ? size.X / (float)size.Y : 1f;
    }

    private static void RenderItemScene(Matrix4x4 projection, Vector2D<int> size)
    {
        var eye = BodyEye();

        Matrix4x4 view;
        float bodyYaw;
        var cameraEye = eye;
        if (_firstPerson)
        {
            // The model stays put and the camera looks around it. The game turns the body with
            // the camera, but with no world on screen that would make the one visible thing -
            // the hand and item - rotate in lockstep, so horizontal look could never be seen.
            bodyYaw = BodyYaw;
            var forward = ForwardFrom(_lookYaw, _lookPitch);
            view = Matrix4x4.CreateLookAt(eye, eye + forward, Vector3.UnitY);
        }
        else
        {
            bodyYaw = BodyYaw;
            view = BuildView(PlayerOrbitTarget, out cameraEye);
        }

        _scene.Render(view * projection, eye, bodyYaw, 0f, Player, Editor, (float)_time, size, _firstPerson);
        _scene.RenderSliceTrail(_playerAnim.Trail, _playerAnim.Elapsed, view * projection, cameraEye);
    }

    // Poses the selected blade exactly as DebugSceneRenderer draws it (the editor's live display, scale
    // and grip, the same eye, body yaw and back offset), so the trail follows the sliders. False while
    // the ITEMS tab previews anything but a slicing or sword item.
    private static bool SampleTrail(PlayerModel model, out Matrix4x4 blade, out Matrix4x4 owner)
    {
        blade = owner = Matrix4x4.Identity;
        if (_tab != StudioTab.Items || !Editor.HasSelection || !Editor.IsPureItem) return false;
        if (ItemRegistry.TryGet(Editor.SelectedId) is not { Attack: AttackPattern.Slice or AttackPattern.Sword } item)
            return false;

        var eye = BodyEye();
        var back = _firstPerson ? PlayerModel.FirstPersonBodyBack : 0f;
        blade = HeldItemPose.Pose(model.GetHandAnchorMatrix(eye, BodyYaw, back, 0f),
            Editor.DisplayMatrix(model.HeldItemRotationOffset), Editor.ItemHeldScale, Editor.ItemGrip);
        owner = SliceTrail.OwnerFrameAt(eye, model.DrawnBodyYaw(BodyYaw));
        _playerAnim.Trail.Color = item.TrailColorOrDefault;
        return true;
    }

    private static void CaptureScreenshot(Vector2D<int> size)
    {
        if (_screenshotPath is not { } path || ++_frameCount < 5) return;
        // A scripted instant: wait until that much animation has been simulated.
        if (_shotAt is { } at && _simulated < at) return;

        Console.WriteLine($"[dev-studio] capturing {size.X}x{size.Y} to {path}");
        Console.Out.Flush();
        var pixels = VisualCapture.ReadPixels(_gl, size.X, size.Y);
        VisualCapture.QueueSave(pixels, size.X, size.Y, path);
        VisualCapture.WaitForPending();
        Console.WriteLine("[dev-studio] capture done");
        Console.Out.Flush();
        _closing = true;
    }

    private static Vector3 BodyEye() => _modelOffset + new Vector3(0f, Camera.EyeHeight, 0f);

    private static Matrix4x4 BuildView(Vector3 orbitTarget, out Vector3 eye) =>
        BuildView(orbitTarget, _distance, out eye);

    private static Matrix4x4 BuildView(Vector3 orbitTarget, float distance, out Vector3 eye)
    {
        var target = orbitTarget + _pan;
        eye = target + OrbitDirection() * distance;
        return Matrix4x4.CreateLookAt(eye, target, Vector3.UnitY);
    }

    private static Vector3 OrbitDirection()
    {
        var yaw = _orbitYaw * MathF.PI / 180f;
        var pitch = _orbitPitch * MathF.PI / 180f;
        return new Vector3(
            MathF.Cos(pitch) * MathF.Sin(yaw),
            MathF.Sin(pitch),
            MathF.Cos(pitch) * MathF.Cos(yaw));
    }

    private static void Orbit(Vector2 delta)
    {
        _orbitYaw -= delta.X * 0.4f;
        _orbitPitch = Math.Clamp(_orbitPitch + delta.Y * 0.4f, -85f, 85f);
    }

    private static void Look(Vector2 delta)
    {
        const float sensitivity = 0.2f;
        _lookYaw += delta.X * sensitivity;
        _lookPitch = Math.Clamp(_lookPitch - delta.Y * sensitivity, -89f, 89f);
    }

    // Camera.GetForwardVector's convention: yaw 0 faces +X, pitch positive looks up.
    private static Vector3 ForwardFrom(float yawDeg, float pitchDeg)
    {
        var yaw = yawDeg * MathF.PI / 180f;
        var pitch = pitchDeg * MathF.PI / 180f;
        return Vector3.Normalize(new Vector3(
            MathF.Cos(pitch) * MathF.Cos(yaw),
            MathF.Sin(pitch),
            MathF.Cos(pitch) * MathF.Sin(yaw)));
    }

    private static void Pan(Vector2 delta)
    {
        var (right, up, scale) = ScreenBasis();
        _pan += right * (-delta.X * scale) + up * (delta.Y * scale);
    }

    private static void MoveModel(Vector2 delta)
    {
        // Opposite world signs to Pan: moving the camera moves the scene the other way, so the
        // model must translate itself along the drag to follow the cursor by the same amount.
        var (right, up, scale) = ScreenBasis();
        _modelOffset += right * (delta.X * scale) - up * (delta.Y * scale);
    }

    // Camera-plane axes plus a distance-scaled pixel step, shared by pan and model move.
    private static (Vector3 Right, Vector3 Up, float Scale) ScreenBasis()
    {
        var forward = -OrbitDirection();
        var right = Vector3.Normalize(Vector3.Cross(forward, Vector3.UnitY));
        var distance = _tab == StudioTab.Trees ? TreeViewDistance(WindowAspect()) : _distance;
        return (right, Vector3.Cross(right, forward), distance * 0.0016f);
    }

    private static void UpdateAnimation(float dt)
    {
        if (dt <= 0f) return;

        PlayerClock.Update(dt);
    }

    private static void OnMouseDown(IMouse mouse, MouseButton button)
    {
        var position = new Vector2(mouse.Position.X, mouse.Position.Y);

        if (button == MouseButton.Left)
        {
            if (_ui.ButtonAt(position) is { } debugButton)
            {
                HandleButton(debugButton.Button, debugButton.Arg);
                return;
            }
            if (_ui.SliderAt(position) is { } slider)
            {
                _dragSlider = slider.Label;
                slider.Set(DevStudioUi.ValueAt(slider, position.X));
                return;
            }
            if (_ui.SlotAt(position) is { } slot)
            {
                // The slot index belongs to whichever grid the active tab shows.
                switch (_tab)
                {
                    case StudioTab.Entities:
                        _entities.Select(slot);
                        EntityClock.Reset();
                        break;
                    case StudioTab.Trees:
                        _trees.Select(slot);
                        break;
                    case StudioTab.Quests:
                        _quests.Pick(_ui.Entries[slot].Id);
                        break;
                    default:
                        Editor.Select(_ui.Entries[slot].Id);
                        _status = "";
                        break;
                }
                return;
            }
            if (_tab == StudioTab.Quests)
            {
                _quests.MouseDown(position, _time, Held(Key.ControlLeft, Key.ControlRight),
                    Held(Key.ShiftLeft, Key.ShiftRight));
                return;
            }
            _leftDrag = true;
            SetCursorCaptured(true);
            return;
        }

        if (button == MouseButton.Right) _rightDrag = true;
        else if (button == MouseButton.Middle) _middleDrag = true;
    }

    private static void OnMouseUp(IMouse mouse, MouseButton button)
    {
        if (button == MouseButton.Left)
        {
            _quests.MouseUp(new Vector2(mouse.Position.X, mouse.Position.Y));
            _leftDrag = false;
            _dragSlider = null;
            SetCursorCaptured(false);
        }
        else if (button == MouseButton.Right) _rightDrag = false;
        else if (button == MouseButton.Middle) _middleDrag = false;
    }

    private static void OnMouseMove(IMouse mouse, Vector2 position)
    {
        if (_tab == StudioTab.Quests) _quests.MouseMove(position, Held(Key.AltLeft, Key.AltRight));
        if (!_haveMouse)
        {
            _lastMouse = position;
            _haveMouse = true;
            return;
        }
        _mouseDelta += position - _lastMouse;
        _lastMouse = position;
    }

    private static void SetCursorCaptured(bool captured)
    {
        if (_captured == captured) return;
        _captured = captured;
        if (_input?.Mouse is { } mouse)
            mouse.Cursor.CursorMode = captured ? CursorMode.Disabled : CursorMode.Normal;
        // Swallow the jump the mode switch causes instead of turning it into a look.
        _haveMouse = false;
        _mouseDelta = Vector2.Zero;
    }

    private static void OnScroll(IMouse mouse, ScrollWheel wheel)
    {
        if (_ui.OverGrid(new Vector2(mouse.Position.X, mouse.Position.Y)))
            _ui.ScrollBy(wheel.Y > 0 ? -1 : 1);
        else if (_tab == StudioTab.Quests)
            _quests.Scroll(new Vector2(mouse.Position.X, mouse.Position.Y), wheel.Y);
        else if (_tab == StudioTab.Trees)
            _treeZoom = Math.Clamp(_treeZoom * (wheel.Y > 0 ? 0.9f : 1.1f), 0.3f, 3f);
        else
            _distance = Math.Clamp(_distance - wheel.Y * 0.4f, 1.2f, 20f);
    }

    private static void HandleButton(DebugButton button, int arg)
    {
        switch (button)
        {
            case DebugButton.TabItems:
                _tab = StudioTab.Items;
                break;
            case DebugButton.TabEntities:
                _tab = StudioTab.Entities;
                break;
            case DebugButton.TabTrees:
                _tab = StudioTab.Trees;
                break;
            case DebugButton.TabQuests:
                _tab = StudioTab.Quests;
                break;
            case DebugButton.TreeStepDown:
                _trees.StepBy(-1);
                break;
            case DebugButton.TreeStepUp:
                _trees.StepBy(1);
                break;
            case DebugButton.TreeMature:
                _trees.Mature();
                break;
            case DebugButton.TreeRegrow:
                _trees.Regrow();
                break;
            case DebugButton.EntityMode:
                if (arg >= 0 && arg < _entities.Modes.Count)
                {
                    _entities.SetMode(_entities.Modes[arg]);
                    EntityClock.Reset();
                }
                break;
            case DebugButton.PlayerMode:
                if (arg >= 0 && arg < PlayerPreviewState.AllModes.Count)
                {
                    _playerAnim.SetMode(PlayerPreviewState.AllModes[arg]);
                    PlayerClock.Reset();
                }
                break;
            case DebugButton.Loop:
                ActiveClock.Loop = !ActiveClock.Loop;
                break;
            case DebugButton.FirstPerson:
                _firstPerson = !_firstPerson;
                _lookYaw = BodyYaw;
                _lookPitch = 0f;
                break;
            case DebugButton.ReloadDisplay:
                Editor.ReloadDisplay();
                _status = "Reloaded from JSON";
                break;
            case DebugButton.ResetPreview:
                Editor.ResetPreview();
                _status = "Preview reset";
                break;
            case DebugButton.Save:
                SaveDisplay();
                break;
        }
    }

    private static void SaveDisplay()
    {
        if (Editor.SourcePath is not { } path)
        {
            _status = "Block transforms are not saved";
            return;
        }

        try
        {
            ItemJsonDisplayWriter.Save(path, Editor.Rotation, Editor.Translation, Editor.Scale);
            _status = $"Saved {path}";
            Log.WriteLine(_status);
        }
        catch (Exception e)
        {
            _status = "Save failed: " + e.Message;
            Log.Warn(_status);
        }
    }

    private static bool Held(Key a, Key b) =>
        _input?.Keyboard is { } keyboard && (keyboard.IsKeyPressed(a) || keyboard.IsKeyPressed(b));

    private static void OnClosing()
    {
        if (_quests?.State.Dirty == true)
            Log.Warn("DevStudio closed with unsaved quest edits; they are lost.");
        _scene?.Dispose();
        _input?.Dispose();
        Log.Shutdown();
    }
}
