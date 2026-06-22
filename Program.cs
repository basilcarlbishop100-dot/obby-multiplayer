// MULTIPLAYER OBBY — with real 3D model, kill bricks & ladders
// -----------------------------------------------
// HOST:   dotnet run -- host
// CLIENT: dotnet run -- client 192.168.X.X
// -----------------------------------------------
// Controls:
//   WASD        — move
//   SPACE       — jump
//   W (on ladder)— climb up
//   S (on ladder)— climb down
//   Q/E or ←/→  — rotate camera
//   ESC         — quit
// -----------------------------------------------

using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using Raylib_cs;
using System.Numerics;
using System.Globalization;

class Program
{
    // =============================================
    // NETWORK
    // =============================================
    const int   PORT      = 7777;
    const float SEND_RATE = 1f / 30f;
    static UdpClient  udp = null!;
    static IPEndPoint remoteEP = null!;
    static bool       connected = false;
    static bool       isHost    = false;
    static float      sendTimer = 0f;

    // =============================================
    // LEVEL OBJECTS
    // =============================================
    enum ObjKind { Platform, Kill, Ladder }

    struct LevelObj
    {
        public Vector3  Center;
        public Vector3  Size;
        public ObjKind  Kind;
        public Color    DrawColor; // fallback color if no model
    }

    static List<LevelObj> levelObjs = new();

    // Starting spawn position (lowest platform cluster)
    static Vector3 SPAWN = new Vector3(-210.9f, -26.5f, 138.0f);
    static Vector3 customSpawn = new Vector3(-210.9f, -26.5f, 138.0f);

    static void BuildLevel()
    {
        levelObjs.Clear();

        // --- Platforms (small objects only, exclude giant merged meshes) ---
        // Only include objects where all three size dims < 80 units (giant ones are merged meshes)
        var raw = new (float cx, float cy, float cz, float sx, float sy, float sz, ObjKind kind)[]
        {
            // == STARTING AREA (bottom cluster) ==
            (-210.3f,-57.9f,138.3f, 4f,1f,4f, ObjKind.Platform),
            (-182.3f,-57.9f,138.3f, 4f,1f,4f, ObjKind.Platform),
            (-184.3f,-57.9f,138.3f, 4f,1f,4f, ObjKind.Platform),
            (-208.3f,-57.8f,138.3f, 4f,1f,4f, ObjKind.Platform),
            (-202.3f,-57.8f,138.3f, 4f,1f,4f, ObjKind.Platform),
            (-204.3f,-57.8f,138.3f, 4f,1f,4f, ObjKind.Platform),
            (-206.3f,-57.8f,138.3f, 4f,1f,4f, ObjKind.Platform),
            (-200.3f,-57.8f,138.3f, 4f,1f,4f, ObjKind.Platform),
            (-177.3f,-57.7f,138.2f, 3f,1f,3f, ObjKind.Platform),
            (-173.3f,-57.7f,138.2f, 4f,1f,4f, ObjKind.Platform),
            (-171.3f,-57.7f,138.2f, 4f,1f,4f, ObjKind.Platform),
            (-175.3f,-57.7f,138.2f, 4f,1f,4f, ObjKind.Platform),

            // == STEPPING STONES GOING UP ==
            (-210.3f,-55.7f,137.7f, 3f,1f,2f, ObjKind.Platform),
            (-210.3f,-53.7f,137.7f, 3f,1f,2f, ObjKind.Platform),
            (-210.3f,-51.7f,137.7f, 3f,1f,2f, ObjKind.Platform),
            (-210.3f,-49.7f,137.7f, 3f,1f,2f, ObjKind.Platform),
            (-210.3f,-47.7f,137.7f, 3f,1f,2f, ObjKind.Platform),
            (-210.3f,-45.7f,137.7f, 3f,1f,2f, ObjKind.Platform),
            (-199.8f,-43.9f,137.8f, 4f,1f,5f, ObjKind.Platform),
            (-210.3f,-43.7f,137.7f, 3f,1f,2f, ObjKind.Platform),
            (-210.3f,-41.7f,137.7f, 3f,1f,2f, ObjKind.Platform),
            (-210.3f,-39.7f,137.7f, 3f,1f,2f, ObjKind.Platform),

            // == KILL BRICK SECTION ==
            (-210.2f,-36.8f,137.8f, 3f,1f,2.5f, ObjKind.Kill),
            (-210.2f,-34.8f,137.8f, 3f,1f,2.5f, ObjKind.Kill),
            (-210.2f,-32.8f,137.8f, 3f,1f,2.5f, ObjKind.Kill),

            // Skip past killers — safe platform to jump to
            (-210.9f,-29.7f,138.0f, 4f,1f,4f, ObjKind.Platform),
            (-210.9f,-25.7f,138.0f, 4f,1f,4f, ObjKind.Platform),

            // == BIG SAFE PLATFORM ==
            (-163.4f,-31.1f,140.6f, 30f,1f,5f, ObjKind.Platform),

            // == LADDER SECTION ==
            // Ladder object (players climb this)
            (-210.3f,-18.7f,138.2f, 3f,20f,3f, ObjKind.Ladder),

            // Platforms above ladder
            (-210.3f,-15.2f,138.1f, 2.5f,1f,3f, ObjKind.Platform),
            (-210.3f,-13.2f,138.1f, 2.5f,1f,3f, ObjKind.Platform),
            (-210.3f,-11.2f,138.1f, 2.5f,1f,3f, ObjKind.Platform),
            (-210.3f,-9.2f,138.1f,  2.5f,1f,3f, ObjKind.Platform),
            (-210.3f,-7.2f,138.1f,  2.5f,1f,3f, ObjKind.Platform),

            // == MORE PLATFORMS ==
            (-210.1f,-5.2f,137.7f, 3f,1f,2f, ObjKind.Platform),
            (-210.1f,-3.2f,137.7f, 3f,1f,2f, ObjKind.Platform),
            (-210.1f,-1.2f,137.7f, 3f,1f,2f, ObjKind.Platform),
            (-210.1f, 0.8f,137.7f, 3f,1f,2f, ObjKind.Platform),

            // Kill bricks in middle
            (-210.26f, 2.78f,138.31f, 3f,1f,3f, ObjKind.Kill),
            (-210.26f, 6.78f,138.31f, 3f,1f,3f, ObjKind.Kill),
            (-210.26f, 8.78f,138.31f, 3f,1f,3f, ObjKind.Kill),
            (-210.26f,10.78f,138.31f, 3f,1f,3f, ObjKind.Kill),
            (-210.26f,12.78f,138.31f, 3f,1f,3f, ObjKind.Kill),

            // Safe platforms to navigate around killers
            (-210.0f,14.7f,137.7f, 3f,1f,3f, ObjKind.Platform),
            (-210.4f,16.8f,138.1f, 3f,1f,3f, ObjKind.Platform),

            // == WIDE SECTION ==
            (-166.8f,20.8f,142.6f, 15f,1f,4f, ObjKind.Platform),

            // == FINAL PLATFORMS ==
            (-200.8f,20.9f,138.6f, 3f,1f,3f, ObjKind.Platform),
            (-196.8f,20.9f,138.6f, 3f,1f,3f, ObjKind.Platform),
            (-202.8f,20.9f,138.6f, 3f,1f,3f, ObjKind.Platform),
            (-194.3f,21.3f,138.2f, 3f,1f,3f, ObjKind.Platform),
            (-189.3f,21.3f,138.0f, 3f,1f,3f, ObjKind.Platform),
            (-208.3f,21.3f,138.2f, 5f,1f,5f, ObjKind.Platform), // FINISH
        };

        foreach (var r in raw)
        {
            Color col = r.kind switch {
                ObjKind.Kill    => new Color(255, 40, 40, 255),
                ObjKind.Ladder  => new Color(180, 120, 40, 255),
                _               => new Color(
                    80 + (Math.Abs((int)(r.cx * 7)) % 120),
                    80 + (Math.Abs((int)(r.cy * 11)) % 120),
                    80 + (Math.Abs((int)(r.cz * 13)) % 120),
                    255)
            };
            levelObjs.Add(new LevelObj {
                Center    = new Vector3(r.cx, r.cy, r.cz),
                Size      = new Vector3(r.sx, r.sy, r.sz),
                Kind      = r.kind,
                DrawColor = col
            });
        }
    }

    static int FinishIndex => levelObjs.Count - 1;

    // =============================================
    // PLAYER STATE
    // =============================================
    struct PlayerState
    {
        public Vector3 Pos;
        public Vector3 Vel;
        public bool    OnGround;
        public bool    OnLadder;
        public int     Checkpoint;
        public bool    Won;
    }

    static PlayerState me;
    static PlayerState them;
    static float       theirLastSeen = -999f;
    static float       totalTime     = 0f;

    static void Respawn(ref PlayerState p)
    {
        if (p.Checkpoint == 0)
        {
            // Always respawn at the designated spawn point
            p.Pos      = customSpawn;
            p.Vel      = Vector3.Zero;
            p.OnGround = false;
            p.OnLadder = false;
            return;
        }
        int idx = Math.Clamp(p.Checkpoint, 0, levelObjs.Count - 1);
        var obj  = levelObjs[idx];
        p.Pos      = new Vector3(obj.Center.X, obj.Center.Y + obj.Size.Y / 2f + 1.2f, obj.Center.Z);
        p.Vel      = Vector3.Zero;
        p.OnGround = false;
        p.OnLadder = false;
    }

    // =============================================
    // PHYSICS
    // =============================================
    const float GRAVITY      = -18f;
    const float JUMP_FORCE   =  18f;
    const float MOVE_SPEED   =  10f;
    const float MOVE_ACCEL   =  20f;
    const float MOVE_DAMP    =  14f;
    const float LADDER_SPEED =   6f;

    static void UpdatePlayer(ref PlayerState p, float dt, float camYaw)
    {
        if (p.Won) return;

        float yawRad = camYaw * MathF.PI / 180f;
        Vector3 fwd  = new Vector3(-MathF.Sin(yawRad), 0, -MathF.Cos(yawRad));
        Vector3 rgt  = new Vector3( MathF.Cos(yawRad), 0, -MathF.Sin(yawRad));

        // --- Check if inside a ladder ---
        p.OnLadder = false;
        foreach (var obj in levelObjs)
        {
            if (obj.Kind != ObjKind.Ladder) continue;
            if (InAABB(p.Pos, obj.Center, obj.Size + new Vector3(0.6f, 0f, 0.6f)))
            {
                p.OnLadder = true;
                break;
            }
        }

        if (p.OnLadder)
        {
            // On ladder: free Y movement, lock horizontal
            p.Vel.X = 0; p.Vel.Z = 0;
            p.Vel.Y = 0;
            if (Raylib.IsKeyDown(KeyboardKey.W)) p.Vel.Y =  LADDER_SPEED;
            if (Raylib.IsKeyDown(KeyboardKey.S)) p.Vel.Y = -LADDER_SPEED;
            if (Raylib.IsKeyPressed(KeyboardKey.Space)) { p.Vel.Y = JUMP_FORCE; p.OnLadder = false; }
        }
        else
        {
            // Normal horizontal movement
            Vector3 wish = Vector3.Zero;
            if (Raylib.IsKeyDown(KeyboardKey.W)) wish += fwd;
            if (Raylib.IsKeyDown(KeyboardKey.S)) wish -= fwd;
            if (Raylib.IsKeyDown(KeyboardKey.A)) wish -= rgt;
            if (Raylib.IsKeyDown(KeyboardKey.D)) wish += rgt;

            if (wish.LengthSquared() > 0) wish = Vector3.Normalize(wish);

            p.Vel.X = Lerp(p.Vel.X, wish.X * MOVE_SPEED, MOVE_ACCEL * dt);
            p.Vel.Z = Lerp(p.Vel.Z, wish.Z * MOVE_SPEED, MOVE_ACCEL * dt);

            if (wish.LengthSquared() == 0)
            {
                p.Vel.X = Lerp(p.Vel.X, 0, MOVE_DAMP * dt);
                p.Vel.Z = Lerp(p.Vel.Z, 0, MOVE_DAMP * dt);
            }

            // Gravity
            p.Vel.Y += GRAVITY * dt;

            // Jump
            if (Raylib.IsKeyPressed(KeyboardKey.Space) && p.OnGround)
            {
                p.Vel.Y = JUMP_FORCE;
                p.OnGround = false;
            }
        }

        // Move
        p.Pos += p.Vel * dt;

        // --- Collision ---
        p.OnGround = false;
        float playerR = 0.45f;  // horizontal radius
        float playerH = 1.8f;   // player height

        for (int i = 0; i < levelObjs.Count; i++)
        {
            var obj = levelObjs[i];
            if (obj.Kind == ObjKind.Ladder) continue;

            float hw  = obj.Size.X / 2f + playerR;
            float hh  = obj.Size.Y / 2f;
            float hd  = obj.Size.Z / 2f + playerR;
            float top = obj.Center.Y + obj.Size.Y / 2f;
            float bot = obj.Center.Y - obj.Size.Y / 2f;

            // Player feet = p.Pos.Y, head = p.Pos.Y + playerH
            float pFeet = p.Pos.Y;
            float pHead = p.Pos.Y + playerH;

            bool overX = p.Pos.X > obj.Center.X - hw && p.Pos.X < obj.Center.X + hw;
            bool overY = pFeet < top && pHead > bot;
            bool overZ = p.Pos.Z > obj.Center.Z - hd && p.Pos.Z < obj.Center.Z + hd;

            if (!overX || !overY || !overZ) continue;

            if (obj.Kind == ObjKind.Kill)
            {
                Respawn(ref p);
                return;
            }

            // Find smallest penetration axis and push out
            float overlapTop   = top  - pFeet;       // push up
            float overlapBot   = pHead - bot;         // push down
            float overlapLeft  = (obj.Center.X + hw) - p.Pos.X;
            float overlapRight = p.Pos.X - (obj.Center.X - hw);
            float overlapFront = (obj.Center.Z + hd) - p.Pos.Z;
            float overlapBack  = p.Pos.Z - (obj.Center.Z - hd);

            float minH = Math.Min(overlapLeft, overlapRight);
            float minV = Math.Min(overlapTop, overlapBot);
            float minZ = Math.Min(overlapFront, overlapBack);

            if (minV < minH && minV < minZ)
            {
                // Vertical resolve
                if (overlapTop < overlapBot)
                {
                    // Land on top
                    p.Pos.Y    = top + 0.001f;
                    p.Vel.Y    = 0f;
                    p.OnGround = true;
                    if (i > p.Checkpoint) p.Checkpoint = i;
                    if (i == FinishIndex && !p.Won) { p.Won = true; Send("WIN"); }
                }
                else
                {
                    // Hit head on bottom
                    p.Pos.Y = bot - playerH - 0.001f;
                    if (p.Vel.Y > 0) p.Vel.Y = 0f;
                }
            }
            else if (minH < minZ)
            {
                // Push out on X
                if (overlapLeft < overlapRight)
                    p.Pos.X = obj.Center.X + hw + 0.001f;
                else
                    p.Pos.X = obj.Center.X - hw - 0.001f;
                p.Vel.X = 0f;
            }
            else
            {
                // Push out on Z
                if (overlapFront < overlapBack)
                    p.Pos.Z = obj.Center.Z + hd + 0.001f;
                else
                    p.Pos.Z = obj.Center.Z - hd - 0.001f;
                p.Vel.Z = 0f;
            }
        }

        // Fall death
        float lowestY = -999f;
        if (p.Checkpoint < levelObjs.Count)
            lowestY = levelObjs[p.Checkpoint].Center.Y - 20f;
        if (p.Pos.Y < lowestY) Respawn(ref p);
    }

    // =============================================
    // MAIN
    // =============================================
    static float camYaw = 0f;

    static void Main(string[] args)
    {
        if (args.Length == 0)
        {
            Console.WriteLine("Usage:");
            Console.WriteLine("  HOST:   dotnet run -- host");
            Console.WriteLine("  CLIENT: dotnet run -- client <host-ip>");
            return;
        }

        isHost = args[0].ToLower() == "host";
        BuildLevel();

        me   = new PlayerState { Pos = customSpawn, Checkpoint = 0 };
        them = new PlayerState { Pos = new Vector3(-207.9f, -26.5f, 138.0f), Checkpoint = 0 };

        // Network
        if (isHost)
        {
            udp      = new UdpClient(PORT);
            remoteEP = new IPEndPoint(IPAddress.Any, 0);
            Console.WriteLine($"[HOST] {GetLocalIP()}:{PORT}");
        }
        else
        {
            if (args.Length < 2) { Console.WriteLine("Provide host IP: dotnet run -- client 192.168.X.X"); return; }
            remoteEP = new IPEndPoint(IPAddress.Parse(args[1].Trim()), PORT);
            udp      = new UdpClient();
            udp.Connect(remoteEP);
            Console.WriteLine($"[CLIENT] → {args[1]}:{PORT}");
            new Thread(() => { while (!connected) { Send("HELLO"); Thread.Sleep(400); } }) { IsBackground = true }.Start();
        }
        new Thread(ReceiveLoop) { IsBackground = true }.Start();

        // Window
        Raylib.InitWindow(1280, 720, isHost ? "OBBY — HOST (Blue)" : "OBBY — CLIENT (Red)");
        Raylib.SetTargetFPS(60);

        // We use geometry boxes for rendering (model coords used for level layout)
        Model? obbyModel = null;
        Console.WriteLine("[INFO] Using geometry rendering.");

        Color myCol    = isHost ? new Color(60, 120, 255, 255) : new Color(255, 80, 80, 255);
        Color theirCol = isHost ? new Color(255, 80, 80, 255)  : new Color(60, 120, 255, 255);

        float camPitch = 20f;
        float camDist  = 20f;

        while (!Raylib.WindowShouldClose())
        {
            float dt = Raylib.GetFrameTime();
            totalTime += dt;

            // Press F to save current position as spawn point
            if (Raylib.IsKeyPressed(KeyboardKey.F))
            {
                customSpawn = me.Pos;
                Console.WriteLine($"[SPAWN SET] {me.Pos.X:F1}, {me.Pos.Y:F1}, {me.Pos.Z:F1}");
            }
            // Press R to respawn at saved spawn
            if (Raylib.IsKeyPressed(KeyboardKey.R))
            {
                me.Pos = customSpawn;
                me.Vel = Vector3.Zero;
            }

            // Camera rotation — mouse drag OR Q/E keys
            if (Raylib.IsMouseButtonDown(MouseButton.Right))
            {
                Vector2 mouseDelta = Raylib.GetMouseDelta();
                camYaw   -= mouseDelta.X * 0.12f;
                camPitch  = Math.Clamp(camPitch + mouseDelta.Y * 0.10f, 5f, 80f);
                Raylib.HideCursor();
            }
            else
            {
                Raylib.ShowCursor();
            }
            // Scroll to zoom
            float scroll = Raylib.GetMouseWheelMove();
            camDist = Math.Clamp(camDist - scroll * 2f, 5f, 60f);

            if (Raylib.IsKeyDown(KeyboardKey.Left)  || Raylib.IsKeyDown(KeyboardKey.Q)) camYaw += 90f * dt;
            if (Raylib.IsKeyDown(KeyboardKey.Right) || Raylib.IsKeyDown(KeyboardKey.E)) camYaw -= 90f * dt;

            // Update my player
            UpdatePlayer(ref me, dt, camYaw);

            // Send
            sendTimer -= dt;
            if (sendTimer <= 0f && connected)
            {
                sendTimer = SEND_RATE;
                Send($"P:{me.Pos.X:F2},{me.Pos.Y:F2},{me.Pos.Z:F2},{me.Checkpoint},{(me.Won?1:0)}");
            }

            // Camera
            float pitchR = camPitch * MathF.PI / 180f;
            float yawR   = camYaw   * MathF.PI / 180f;
            Vector3 camOff = new Vector3(
                MathF.Sin(yawR) * camDist * MathF.Cos(pitchR),
                MathF.Sin(pitchR) * camDist,
                MathF.Cos(yawR)  * camDist * MathF.Cos(pitchR)
            );
            Camera3D cam = new Camera3D(
                me.Pos + camOff,
                me.Pos + new Vector3(0, 0.5f, 0),
                Vector3.UnitY, 60f,
                CameraProjection.Perspective
            );

            // =================== RENDER ===================
            Raylib.BeginDrawing();
            Raylib.ClearBackground(new Color(100, 180, 255, 255));
            Raylib.BeginMode3D(cam);

            // Draw the full 3D model if loaded (as decoration/visual)
            if (obbyModel.HasValue)
            {
                // Scale down and center the model (it's huge in original coordinates)
                // The model spans ~700 units; we want it to roughly match our collision boxes
                Raylib.DrawModel(obbyModel.Value, Vector3.Zero, 1.0f, Color.White);
            }
            else
            {
                // === GRASS GROUND ===
                float groundY = -35f;
                Raylib.DrawCube(new Vector3(-190f, groundY - 0.5f, 138f), 120f, 1f, 30f, new Color(34, 139, 34, 255));
                var rng = new System.Random(42);
                for (int g = 0; g < 120; g++)
                {
                    float gx = -250f + (float)rng.NextDouble() * 120f;
                    float gz = 123f  + (float)rng.NextDouble() * 30f;
                    float gh = 0.4f  + (float)rng.NextDouble() * 0.5f;
                    var gc = new Color(20 + rng.Next(40), 100 + rng.Next(80), 20 + rng.Next(30), 255);
                    Raylib.DrawCube(new Vector3(gx, groundY + gh / 2f, gz), 0.12f, gh, 0.12f, gc);
                    Raylib.DrawCube(new Vector3(gx + 0.1f, groundY + gh * 0.6f, gz + 0.1f), 0.08f, gh * 0.8f, 0.08f, gc);
                }

                // Draw colored boxes for each level object
                foreach (var obj in levelObjs)
                {
                    if (obj.Kind == ObjKind.Kill)
                    {
                        // Pulsing red kill brick
                        float pulse = MathF.Sin(totalTime * 6f) * 0.5f + 0.5f;
                        Color kCol  = new Color(255, (int)(30 + pulse * 60), 30, 255);
                        Raylib.DrawCube(obj.Center, obj.Size.X, obj.Size.Y, obj.Size.Z, kCol);
                        Raylib.DrawCubeWires(obj.Center, obj.Size.X + 0.1f, obj.Size.Y + 0.1f, obj.Size.Z + 0.1f,
                            new Color(255, 80, 80, 200));
                    }
                    else if (obj.Kind == ObjKind.Ladder)
                    {
                        // Brown ladder — draw rungs
                        Raylib.DrawCube(obj.Center, obj.Size.X * 0.15f, obj.Size.Y, obj.Size.Z * 0.15f,
                            new Color(140, 90, 30, 255));
                        int rungs = (int)(obj.Size.Y / 1.5f);
                        for (int r = 0; r < rungs; r++)
                        {
                            float ry = obj.Center.Y - obj.Size.Y / 2f + r * 1.5f + 0.75f;
                            Raylib.DrawCube(new Vector3(obj.Center.X, ry, obj.Center.Z),
                                obj.Size.X, 0.2f, obj.Size.Z * 0.15f,
                                new Color(180, 120, 40, 255));
                        }
                    }
                    else
                    {
                        bool isFinish = (levelObjs.IndexOf(obj) == FinishIndex);
                        Color fc = isFinish
                            ? new Color(50, (int)(180 + MathF.Sin(totalTime * 4) * 75), 80, 255)
                            : obj.DrawColor;
                        Raylib.DrawCube(obj.Center, obj.Size.X, obj.Size.Y, obj.Size.Z, fc);
                        Raylib.DrawCubeWires(obj.Center, obj.Size.X + 0.05f, obj.Size.Y + 0.05f, obj.Size.Z + 0.05f,
                            new Color(255, 255, 255, 25));

                        // Finish flag
                        if (isFinish)
                        {
                            Vector3 fb = obj.Center + new Vector3(0, obj.Size.Y / 2f, 0);
                            Raylib.DrawCylinder(fb, 0.12f, 0.12f, 5f, 6, Color.White);
                            Raylib.DrawCube(fb + new Vector3(0.8f, 4.2f, 0), 1.5f, 0.7f, 0.1f, Color.Gold);
                        }
                    }
                }
            }

            // My player
            DrawPlayer(me.Pos, myCol, me.OnGround, me.OnLadder, totalTime);

            // Their player
            bool theirVis = connected && (totalTime - theirLastSeen) < 3f;
            if (theirVis) DrawPlayer(them.Pos, theirCol, false, false, totalTime);

            Raylib.EndMode3D();

            // =================== HUD ===================
            string role = isHost ? "HOST — Blue" : "CLIENT — Red";
            Raylib.DrawText(role, 20, 16, 24, myCol);

            // Progress
            float prog = (float)me.Checkpoint / (levelObjs.Count - 1);
            Raylib.DrawText("YOUR PROGRESS", 20, 50, 15, Color.Gray);
            Raylib.DrawRectangle(20, 68, 200, 12, new Color(30, 30, 50, 255));
            Raylib.DrawRectangle(20, 68, (int)(200 * prog), 12, myCol);
            Raylib.DrawRectangleLines(20, 68, 200, 12, Color.DarkGray);

            if (theirVis)
            {
                float tProg = (float)them.Checkpoint / (levelObjs.Count - 1);
                Raylib.DrawText("OPPONENT", 20, 86, 15, Color.Gray);
                Raylib.DrawRectangle(20, 102, 200, 12, new Color(30, 30, 50, 255));
                Raylib.DrawRectangle(20, 102, (int)(200 * tProg), 12, theirCol);
                Raylib.DrawRectangleLines(20, 102, 200, 12, Color.DarkGray);
            }

            if (me.OnLadder)
                Raylib.DrawText("🪜 ON LADDER — W/S to climb", 20, 130, 18, new Color(255, 200, 80, 255));

            if (!connected)
            {
                int dots = ((int)(totalTime * 2)) % 4;
                Raylib.DrawText("Waiting for opponent" + new string('.', dots), 20, 155, 18, Color.Yellow);
            }

            if (isHost)
            {
                string ip = GetLocalIP();
                Raylib.DrawText($"Your IP: {ip}", Raylib.GetScreenWidth() - 310, 16, 18, Color.LightGray);
                Raylib.DrawText("Friend:  dotnet run -- client " + ip, Raylib.GetScreenWidth() - 450, 38, 13, Color.Gray);
            }

            // Controls
            Raylib.DrawText("WASD: Move   SPACE: Jump   Right-Click+Drag: Camera   Scroll: Zoom   Q/E: Rotate", 20, Raylib.GetScreenHeight() - 34, 15,
                new Color(100, 100, 130, 255));
            // DEBUG: show player position
            Raylib.DrawText($"POS: {me.Pos.X:F1}, {me.Pos.Y:F1}, {me.Pos.Z:F1}   [F] Set Spawn Here   [R] Respawn", 20, Raylib.GetScreenHeight() - 60, 18, Color.Yellow);

            // Win/Lose
            if (me.Won)
            {
                Raylib.DrawRectangle(0, 0, Raylib.GetScreenWidth(), Raylib.GetScreenHeight(), new Color(0,0,0,160));
                Raylib.DrawText("🏆 YOU WIN!", Raylib.GetScreenWidth()/2 - 180, Raylib.GetScreenHeight()/2 - 50, 72, Color.Gold);
                Raylib.DrawText("Press ESC to quit", Raylib.GetScreenWidth()/2 - 110, Raylib.GetScreenHeight()/2 + 50, 22, Color.LightGray);
            }
            else if (them.Won && theirVis)
            {
                Raylib.DrawRectangle(0, 0, Raylib.GetScreenWidth(), Raylib.GetScreenHeight(), new Color(60,0,0,160));
                Raylib.DrawText("OPPONENT WINS", Raylib.GetScreenWidth()/2 - 230, Raylib.GetScreenHeight()/2 - 40, 60, Color.Red);
            }

            Raylib.EndDrawing();
        }

        Raylib.CloseWindow();
        udp?.Close();
    }

    // =============================================
    // DRAW PLAYER
    // =============================================
    static void DrawPlayer(Vector3 pos, Color col, bool onGround, bool onLadder, float t)
    {
        // Body
        Raylib.DrawCube(pos + new Vector3(0, 0.5f, 0), 0.9f, 1.0f, 0.9f, col);
        Raylib.DrawCubeWires(pos + new Vector3(0, 0.5f, 0), 0.92f, 1.02f, 0.92f, Color.White);
        // Head
        Color headCol = new Color(Math.Min(col.R + 40, 255), Math.Min(col.G + 40, 255), Math.Min(col.B + 40, 255), 255);
        Raylib.DrawCube(pos + new Vector3(0, 1.35f, 0), 0.65f, 0.65f, 0.65f, headCol);
        // Eyes
        Raylib.DrawCube(pos + new Vector3(-0.15f, 1.42f, -0.34f), 0.12f, 0.12f, 0.05f, Color.White);
        Raylib.DrawCube(pos + new Vector3( 0.15f, 1.42f, -0.34f), 0.12f, 0.12f, 0.05f, Color.White);
        // Shadow
        if (onGround)
            Raylib.DrawCircle3D(new Vector3(pos.X, pos.Y + 0.02f, pos.Z), 0.55f, new Vector3(1,0,0), 90f, new Color(0,0,0,70));
        // Ladder glow
        if (onLadder)
        {
            float g = MathF.Sin(t * 8f) * 0.5f + 0.5f;
            Raylib.DrawCubeWires(pos + new Vector3(0, 0.8f, 0), 1.4f, 2.4f, 1.4f,
                new Color(255, (int)(180 + g * 75), 50, (int)(120 + g * 100)));
        }
    }

    // =============================================
    // HELPERS
    // =============================================
    static bool InAABB(Vector3 point, Vector3 center, Vector3 size)
    {
        return MathF.Abs(point.X - center.X) < size.X / 2f &&
               MathF.Abs(point.Y - center.Y) < size.Y / 2f &&
               MathF.Abs(point.Z - center.Z) < size.Z / 2f;
    }

    static float Lerp(float a, float b, float t) => a + (b - a) * t;

    static void Send(string msg)
    {
        try
        {
            byte[] d = Encoding.UTF8.GetBytes(msg);
            if (isHost) udp.Send(d, d.Length, remoteEP);
            else        udp.Send(d, d.Length);
        }
        catch (Exception e) { Console.WriteLine($"[SEND] {e.Message}"); }
    }

    static void ReceiveLoop()
    {
        while (true)
        {
            try
            {
                IPEndPoint ep = new IPEndPoint(IPAddress.Any, 0);
                byte[] data   = udp.Receive(ref ep);
                string msg    = Encoding.UTF8.GetString(data);

                if (msg == "HELLO" && isHost)   { remoteEP = ep; connected = true; Send("ACK"); continue; }
                if (msg == "ACK"   && !isHost)  { connected = true; continue; }
                if (msg == "WIN")               { them.Won = true; if (isHost) remoteEP = ep; continue; }

                if (msg.StartsWith("P:"))
                {
                    if (isHost) remoteEP = ep;
                    string[] p = msg.Substring(2).Split(',');
                    if (p.Length >= 5 &&
                        float.TryParse(p[0], NumberStyles.Float, CultureInfo.InvariantCulture, out float x) &&
                        float.TryParse(p[1], NumberStyles.Float, CultureInfo.InvariantCulture, out float y) &&
                        float.TryParse(p[2], NumberStyles.Float, CultureInfo.InvariantCulture, out float z) &&
                        int.TryParse(p[3], out int ck) &&
                        int.TryParse(p[4], out int won))
                    {
                        them.Pos        = new Vector3(x, y, z);
                        them.Checkpoint = ck;
                        if (won == 1) them.Won = true;
                        theirLastSeen   = totalTime;
                        connected       = true;
                    }
                }
            }
            catch (Exception e) { Console.WriteLine($"[RECV] {e.Message}"); break; }
        }
    }

    static string GetLocalIP()
    {
        try
        {
            using var s = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, 0);
            s.Connect("8.8.8.8", 65530);
            return (s.LocalEndPoint as IPEndPoint)?.Address.ToString() ?? "unknown";
        }
        catch { return "unknown"; }
    }
}
