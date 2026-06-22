using System;
using System.Collections;
using System.Collections.Generic;
using Raylib_cs;
using System.Numerics;

namespace MyNewApp
{
    enum GameState { Flying, BossFight, PlanetApproach, Landing, Upgrading, GameOver }

    class Program
    {
        private struct Obstacle
        {
            public Vector3 Position;
            public float Radius;
            public Color MainColor;
            public Color WireColor;
            public float RotationSpeed;
            public float CurrentRotation;
            public float Health;
        }

        private struct Star
        {
            public Vector3 Position;
            public float SpeedModifier;
        }

        private struct Laser
        {
            public Vector3 Position;
            public bool Active;
            public bool FromWingLeft;
        }

        private struct Particle
        {
            public Vector3 Position;
            public Vector3 Velocity;
            public Color MainColor;
            public float Lifetime;
            public bool Active;
        }

        private static float CustomLerp(float start, float end, float amount)
        {
            return start + (end - start) * amount;
        }

        // --- EMBEDDED FRAGMENT SHADER (GLSL v330) ---
        // This adds subtle scanlines, a dark space vignette, and a slight cosmic color tint
        private const string ScreenShaderCode = @"#version 330
            in vec2 fragTexCoord;
            in vec4 fragColor;
            out vec4 finalColor;
            uniform sampler2D texture0;
            uniform vec4 colDiffuse;
            uniform float time;

            void main() {
                vec4 texel = texture(texture0, fragTexCoord);
                
                // 1. Create a vignette effect (darker edges)
                vec2 uv = fragTexCoord - 0.5;
                float vgn = 1.0 - dot(uv, uv) * 1.3;
                
                // 2. Subtle arcade scanlines based on screen Y
                float scanline = 0.95 + 0.05 * sin(fragTexCoord.y * 720.0 * 3.14159);
                
                // 3. Combine texture color with effects
                vec3 rgb = texel.rgb * vgn * scanline;
                
                // Boost bright colors slightly for an artificial neon glow look
                if (max(rgb.r, max(rgb.g, rgb.b)) > 0.6) {
                    rgb *= 1.15;
                }

                finalColor = vec4(rgb, texel.a) * fragColor * colDiffuse;
            }";

        static void Main(string[] args)
        {
            const int screenWidth  = 1280;
            const int screenHeight = 720;

            Raylib.SetConfigFlags(ConfigFlags.Msaa4xHint);
            Raylib.InitWindow(screenWidth, screenHeight, "STARFIGHTER VELOCITY: TOTAL OVERDRIVE");
            Raylib.SetTargetFPS(60);

            // --- AUDIO ---
            Raylib.InitAudioDevice();

            static unsafe Sound MakeTone(float frequency, float durationSec, float volume, bool square = false, bool sawtooth = false)
            {
                int sampleRate  = 22050;
                int sampleCount = (int)(sampleRate * durationSec);
                int subChunk2Size = sampleCount * 2;
                int chunkSize = 36 + subChunk2Size;
                byte[] wavBytes = new byte[44 + subChunk2Size];

                wavBytes[0] = 0x52; wavBytes[1] = 0x49; wavBytes[2] = 0x46; wavBytes[3] = 0x46;
                BitConverter.GetBytes(chunkSize).CopyTo(wavBytes, 4);
                wavBytes[8] = 0x57; wavBytes[9] = 0x41; wavBytes[10] = 0x56; wavBytes[11] = 0x45;
                wavBytes[12] = 0x66; wavBytes[13] = 0x6D; wavBytes[14] = 0x74; wavBytes[15] = 0x20;
                BitConverter.GetBytes(16).CopyTo(wavBytes, 16);
                BitConverter.GetBytes((short)1).CopyTo(wavBytes, 20);
                BitConverter.GetBytes((short)1).CopyTo(wavBytes, 22);
                BitConverter.GetBytes(sampleRate).CopyTo(wavBytes, 24);
                BitConverter.GetBytes(sampleRate * 2).CopyTo(wavBytes, 28);
                BitConverter.GetBytes((short)2).CopyTo(wavBytes, 32);
                BitConverter.GetBytes((short)16).CopyTo(wavBytes, 34);
                wavBytes[36] = 0x64; wavBytes[37] = 0x61; wavBytes[38] = 0x74; wavBytes[39] = 0x61;
                BitConverter.GetBytes(subChunk2Size).CopyTo(wavBytes, 40);

                for (int i = 0; i < sampleCount; i++)
                {
                    float t = (float)i / sampleRate;
                    float val = sawtooth 
                        ? 2.0f * (t * frequency - MathF.Floor(t * frequency + 0.5f))
                        : (square ? (MathF.Sin(2.0f * MathF.PI * frequency * t) >= 0 ? 1.0f : -1.0f) : MathF.Sin(2.0f * MathF.PI * frequency * t));

                    float fade = i > sampleCount * 0.9f ? 1.0f - (float)(i - sampleCount * 0.9f) / (sampleCount * 0.1f) : 1.0f;
                    short sample = (short)(val * fade * volume * short.MaxValue);
                    int pos = 44 + (i * 2);
                    wavBytes[pos]     = (byte)(sample & 0xFF);
                    wavBytes[pos + 1] = (byte)((sample >> 8) & 0xFF);
                }
                return Raylib.LoadSoundFromWave(Raylib.LoadWaveFromMemory(".wav", wavBytes));
            }

            Sound rumbleSound   = MakeTone(60.0f,  2.5f, 0.45f);
            Sound stingSound    = MakeTone(880.0f, 0.8f, 0.28f, sawtooth: true);
            Sound droneSound    = MakeTone(110.0f, 6.5f, 0.14f, square: true);
            Sound laserSound    = MakeTone(620.0f, 0.12f, 0.22f, sawtooth: true);
            Sound explodeSound  = MakeTone(90.0f,  0.4f, 0.35f, square: true);

            // --- GRAPHICS & SHADER INITIALIZATION ---
            Shader screenShader = Raylib.LoadShaderFromMemory(null, ScreenShaderCode);
            int timeLoc = Raylib.GetShaderLocation(screenShader, "time");
            
            // Create a virtual canvas to render our 3D scene into before applying shaders
            RenderTexture2D targetCanvas = Raylib.LoadRenderTexture(screenWidth, screenHeight);

            // --- VARIABLES ---
            GameState currentState = GameState.Flying;
            int score = 0;
            float survivalTime = 0.0f;
            int killStreak = 0;
            float streakTimer = 0.0f;
            float totalElapsedGameTime = 0.0f; // Track uniform time for animations

            float shipArmor          = 100.0f; // Adjusted to match standard 100% cap
            float shipLengthModifier = 0.0f;
            bool  hasRamp            = false;
            int   upgradeTokens      = 0;

            // Nitro System
            float maxNitro   = 100.0f;
            float currentNitro = 100.0f;
            int   nitroLevel   = 1;
            bool  isBoosting   = false;

            // --- PLANET PROGRESSION VARIABLES ---
            int planetLevel = 1;
            int nextPlanetScore = 10000; // Keeping your customized 10k threshold
            bool planetActive = false;
            Vector3 planetPosition = Vector3.Zero;
            float currentPlanetSize = 60.0f;

            // Boss Attributes
            int  nextBossScore   = 25000;
            Vector3 bossPosition = Vector3.Zero;
            float bossMaxHealth  = 300.0f;
            float bossHealth     = 300.0f;
            float bossSideMove   = 0.0f;
            bool bossLaserActive = false;
            Vector3 bossLaserPos = Vector3.Zero;

            Vector3 shipPosition    = new Vector3(0.0f, 0.0f, 20.0f);
            Vector3 targetVelocity  = Vector3.Zero;
            Vector3 currentVelocity = Vector3.Zero;

            float baseForwardSpeed = 66.0f;
            float forwardSpeed     = 66.0f;
            float strafeSpeed      = 52.0f;
            float smoothFactor     = 14.5f;

            float shipRoll  = 0.0f;
            float shipPitch = 0.0f;
            float currentFOV = 70.0f;

            // --- LASER SYSTEM ---
            const int maxLasers = 20;
            Laser[] lasers = new Laser[maxLasers];
            float fireCooldown = 0.0f;
            bool alternateWing = false;

            // --- PARTICLE SYSTEM ---
            const int maxParticles = 100;
            Particle[] particles = new Particle[maxParticles];
            Random rand = new Random(1337);

            Action<Vector3, Color, int> SpawnExplosion = (pos, color, count) => {
                int spawned = 0;
                for (int i = 0; i < maxParticles; i++)
                {
                    if (!particles[i].Active)
                    {
                        particles[i].Active = true;
                        particles[i].Position = pos;
                        particles[i].Velocity = new Vector3(
                            (float)(rand.NextDouble() - 0.5) * 35.0f,
                            (float)(rand.NextDouble() - 0.5) * 35.0f,
                            (float)(rand.NextDouble() - 0.5) * 35.0f
                        );
                        particles[i].MainColor = color;
                        particles[i].Lifetime = (float)rand.NextDouble() * 0.6f + 0.2f;
                        spawned++;
                        if (spawned >= count) break;
                    }
                }
            };

            // --- CUTSCENE ---
            float cutsceneTimer = 0.0f;
            const float CUTSCENE_DURATION = 6.5f;
            float screenShake = 0.0f;
            float cutsceneCamZoom = 70.0f;
            Vector3 cutsceneCamPos = Vector3.Zero;
            Vector3 cutsceneCamTarget = Vector3.Zero;
            bool cutsceneInitialized = false;
            bool stingPlayed = false;
            Random shakeRand = new Random();

            // --- ENVIRONMENT ---
            const int maxObstacles = 55;
            Obstacle[] obstacles = new Obstacle[maxObstacles];

            Action ResetObstacles = () => {
                for (int i = 0; i < maxObstacles; i++)
                {
                    obstacles[i] = new Obstacle {
                        Position = new Vector3(
                            (float)rand.NextDouble() * 84.0f - 42.0f,
                            (float)rand.NextDouble() * 54.0f - 27.0f,
                            shipPosition.Z - ((float)rand.NextDouble() * 240.0f + 60.0f)
                        ),
                        Radius = (float)rand.NextDouble() * 4.2f + 1.2f,
                        MainColor = new Color(rand.Next(40, 70), rand.Next(35, 45), rand.Next(40, 50), 255),
                        WireColor = new Color(0, rand.Next(160, 240), rand.Next(200, 255), 255),
                        RotationSpeed = (float)rand.NextDouble() * 50.0f - 25.0f,
                        CurrentRotation = (float)rand.NextDouble() * 360.0f,
                        Health = 20.0f
                    };
                }
            };
            ResetObstacles();

            const int maxStars = 150;
            Star[] spaceDust = new Star[maxStars];
            for (int i = 0; i < maxStars; i++)
            {
                spaceDust[i] = new Star {
                    Position = new Vector3((float)rand.NextDouble() * 110.0f - 55.0f, (float)rand.NextDouble() * 70.0f - 35.0f, (float)rand.NextDouble() * -220.0f),
                    SpeedModifier = (float)rand.NextDouble() * 1.6f + 0.4f
                };
            }
// ==========================================
            // MAIN LOOP
            // ==========================================
            while (!Raylib.WindowShouldClose())
            {
                float deltaTime = Raylib.GetFrameTime();
                totalElapsedGameTime += deltaTime;

                // Decay streak
                if (killStreak > 0)
                {
                    streakTimer -= deltaTime;
                    if (streakTimer <= 0) killStreak = 0;
                }

                // Particles Update
                for (int i = 0; i < maxParticles; i++)
                {
                    if (particles[i].Active)
                    {
                        particles[i].Position += particles[i].Velocity * deltaTime;
                        particles[i].Lifetime -= deltaTime;
                        if (particles[i].Lifetime <= 0) particles[i].Active = false;
                    }
                }

                // ==========================================
                // LOGIC PROCESSOR
                // ==========================================
                if (currentState == GameState.Flying || currentState == GameState.BossFight)
                {
                    survivalTime += deltaTime;
                    int multi = 1 + (killStreak / 4) + (isBoosting ? 1 : 0);
                    score += (int)(deltaTime * 80 * multi);

                    // Check Boss Milestone Trigger
                    if (score >= nextBossScore && currentState == GameState.Flying)
                    {
                        currentState = GameState.BossFight;
                        bossPosition = new Vector3(0.0f, 15.0f, shipPosition.Z - 180.0f);
                        bossHealth = bossMaxHealth;
                        planetActive = false; 
                    }

                    // Check Planet Trigger 
                    if (score >= nextPlanetScore && !planetActive && currentState == GameState.Flying)
                    {
                        planetActive = true;
                        float distance = 850.0f + (planetLevel * 450.0f);
                        planetPosition = new Vector3(0, 0, shipPosition.Z - distance);
                    }

                    if (planetActive && (shipPosition.Z - planetPosition.Z) < 350.0f && currentState == GameState.Flying)
                    {
                        currentState = GameState.PlanetApproach;
                        cutsceneTimer = 0.0f;
                        cutsceneInitialized = false;
                        stingPlayed = false;
                        isBoosting = false;
                    }

                    // --- NITRO SPEED FLUIDITY ---
                    if (Raylib.IsKeyDown(KeyboardKey.LeftShift) && currentNitro > 0)
                    {
                        isBoosting = true;
                        forwardSpeed = baseForwardSpeed * 1.9f;
                        currentNitro -= 28.0f * deltaTime;
                        currentFOV = CustomLerp(currentFOV, 88.0f, 8.0f * deltaTime);
                    }
                    else
                    {
                        isBoosting = false;
                        forwardSpeed = baseForwardSpeed;
                        currentFOV = CustomLerp(currentFOV, 70.0f, 6.0f * deltaTime);
                    }
                    if (currentNitro < 0) currentNitro = 0;

                    // --- WEAPONS CANNON LOGIC ---
                    if (fireCooldown > 0) fireCooldown -= deltaTime;
                    if (Raylib.IsKeyDown(KeyboardKey.Space) && fireCooldown <= 0)
                    {
                        fireCooldown = 0.16f;
                        for (int i = 0; i < maxLasers; i++)
                        {
                            if (!lasers[i].Active)
                            {
                                lasers[i].Active = true;
                                lasers[i].FromWingLeft = alternateWing;
                                lasers[i].Position = shipPosition + new Vector3(alternateWing ? -2.0f : 2.0f, -0.1f, -2.0f);
                                alternateWing = !alternateWing;
                                Raylib.PlaySound(laserSound);
                                break;
                            }
                        }
                    }

                    // Update Lasers
                    for (int i = 0; i < maxLasers; i++)
                    {
                        if (lasers[i].Active)
                        {
                            lasers[i].Position.Z -= 280.0f * deltaTime; 
                            if (lasers[i].Position.Z < shipPosition.Z - 300.0f) lasers[i].Active = false;
                        }
                    }

                    // --- BOSS CONTROLLER ---
                    if (currentState == GameState.BossFight)
                    {
                        bossPosition.Z = shipPosition.Z - 140.0f; 
                        bossSideMove += deltaTime * 1.8f;
                        bossPosition.X = MathF.Sin(bossSideMove) * 25.0f;
                        bossPosition.Y = MathF.Cos(bossSideMove * 0.5f) * 8.0f + 4.0f;

                        // Boss warning shots
                        if (rand.Next(0, 40) == 5)
                        {
                            bossLaserActive = true;
                            bossLaserPos = bossPosition;
                        }

                        if (bossLaserActive)
                        {
                            bossLaserPos.Z += 140.0f * deltaTime;
                            if (Vector3.Distance(shipPosition, bossLaserPos) < 4.5f)
                            {
                                shipArmor -= 20.0f;
                                bossLaserActive = false;
                                if (shipArmor <= 0) currentState = GameState.GameOver;
                            }
                            if (bossLaserPos.Z > shipPosition.Z + 20.0f) bossLaserActive = false;
                        }

                        // Check Laser hits on Boss
                        for (int l = 0; l < maxLasers; l++)
                        {
                            if (lasers[l].Active && Vector3.Distance(lasers[l].Position, bossPosition) < 14.0f)
                            {
                                lasers[l].Active = false;
                                bossHealth -= 10.0f;
                                SpawnExplosion(lasers[l].Position, Color.Red, 3);
                                if (bossHealth <= 0)
                                {
                                    SpawnExplosion(bossPosition, Color.Gold, 45);
                                    Raylib.PlaySound(explodeSound);
                                    score += 5000;
                                    nextBossScore += 35000;
                                    nextPlanetScore = score + 2000; 
                                    currentState = GameState.Flying;
                                }
                            }
                        }
                    }

                    // --- MOVEMENT INPUT ---
                    targetVelocity.X = 0.0f; targetVelocity.Y = 0.0f;
                    if (Raylib.IsKeyDown(KeyboardKey.A)) targetVelocity.X = -strafeSpeed;
                    if (Raylib.IsKeyDown(KeyboardKey.D)) targetVelocity.X =  strafeSpeed;
                    if (Raylib.IsKeyDown(KeyboardKey.W)) targetVelocity.Y =  strafeSpeed;
                    if (Raylib.IsKeyDown(KeyboardKey.S)) targetVelocity.Y = -strafeSpeed;

                    currentVelocity.X = CustomLerp(currentVelocity.X, targetVelocity.X, smoothFactor * deltaTime);
                    currentVelocity.Y = CustomLerp(currentVelocity.Y, targetVelocity.Y, smoothFactor * deltaTime);

                    shipRoll  = CustomLerp(shipRoll,  -currentVelocity.X * 0.5f, 10.0f * deltaTime);
                    shipPitch = CustomLerp(shipPitch,  currentVelocity.Y * 0.4f, 10.0f * deltaTime);

                    shipPosition.X += currentVelocity.X * deltaTime;
                    shipPosition.Y += currentVelocity.Y * deltaTime;
                    shipPosition.Z -= forwardSpeed * deltaTime;

                    shipPosition.X = Math.Clamp(shipPosition.X, -45.0f, 45.0f);
                    shipPosition.Y = Math.Clamp(shipPosition.Y, -28.0f, 28.0f);

                    // --- OBSTACLES & COLLISION ENGINE ---
                    for (int i = 0; i < maxObstacles; i++)
                    {
                        obstacles[i].CurrentRotation += obstacles[i].RotationSpeed * deltaTime;

                        if (obstacles[i].Position.Z > shipPosition.Z + 20.0f)
                        {
                            obstacles[i].Position.Z = shipPosition.Z - 240.0f;
                            obstacles[i].Position.X = (float)rand.NextDouble() * 84.0f - 42.0f;
                            obstacles[i].Position.Y = (float)rand.NextDouble() * 54.0f - 27.0f;
                            obstacles[i].Health = 20.0f;
                        }

                        // Laser hits asteroid
                        for (int l = 0; l < maxLasers; l++)
                        {
                            if (lasers[l].Active && Vector3.Distance(lasers[l].Position, obstacles[i].Position) < (obstacles[i].Radius + 1.0f))
                            {
                                lasers[l].Active = false;
                                obstacles[i].Health -= 10.0f;
                                if (obstacles[i].Health <= 0)
                                {
                                    SpawnExplosion(obstacles[i].Position, obstacles[i].WireColor, 8);
                                    Raylib.PlaySound(explodeSound);
                                    obstacles[i].Position.Z = shipPosition.Z - 240.0f; 
                                    killStreak++;
                                    streakTimer = 3.5f;
                                    score += 250 * killStreak;
                                }
                            }
                        }

                        // Ship crashes into asteroid
                        float distance = Vector3.Distance(shipPosition, obstacles[i].Position);
                        if (distance < (obstacles[i].Radius + 1.2f))
                        {
                            shipArmor -= hasRamp ? 10.0f : 25.0f; 
                            SpawnExplosion(obstacles[i].Position, Color.Orange, 12);
                            obstacles[i].Position.Z = shipPosition.Z - 240.0f;

                            if (shipArmor <= 0) currentState = GameState.GameOver;
                        }
                    }
                }
                else if (currentState == GameState.PlanetApproach)
                {
                    cutsceneTimer += deltaTime;
                    if (!cutsceneInitialized)
                    {
                        cutsceneInitialized = true;
                        Raylib.PlaySound(rumbleSound);
                        Raylib.PlaySound(droneSound);
                    }

                    float t = cutsceneTimer / CUTSCENE_DURATION;
                    shipPosition.Z -= baseForwardSpeed * deltaTime;

                    if (t < 0.3f)
                    {
                        float p = t / 0.3f;
                        cutsceneCamPos = new Vector3(shipPosition.X * 0.85f, shipPosition.Y * 0.85f + 4.5f + CustomLerp(0, 12.0f, p), shipPosition.Z + CustomLerp(12.0f, 28.0f, p));
                        cutsceneCamTarget = new Vector3(CustomLerp(shipPosition.X, planetPosition.X, p * 0.4f), CustomLerp(shipPosition.Y, planetPosition.Y - 10.0f, p), shipPosition.Z - 20.0f);
                        cutsceneCamZoom = CustomLerp(70.0f, 55.0f, p);
                    }
                    else if (t < 0.5f)
                    {
                        float p = (t - 0.3f) / 0.2f;
                        if (!stingPlayed) { Raylib.PlaySound(stingSound); stingPlayed = true; }
                        screenShake = CustomLerp(8.0f, 0.0f, p);
                        float sx = (float)(shakeRand.NextDouble() - 0.5) * screenShake;
                        float sy = (float)(shakeRand.NextDouble() - 0.5) * screenShake;
                        cutsceneCamPos = new Vector3(shipPosition.X * 0.85f + sx, shipPosition.Y * 0.85f + 16.5f + sy, shipPosition.Z + 28.0f);
                        cutsceneCamTarget = planetPosition;
                        cutsceneCamZoom = CustomLerp(55.0f, 85.0f, p);
                    }
                    else if (t < 0.85f)
                    {
                        float p = (t - 0.5f) / 0.35f;
                        cutsceneCamPos = new Vector3(CustomLerp(shipPosition.X * 0.85f, planetPosition.X, p * 0.3f), CustomLerp(shipPosition.Y * 0.85f + 16.5f, planetPosition.Y + 20.0f, p), CustomLerp(shipPosition.Z + 28.0f, planetPosition.Z + 120.0f, p));
                        cutsceneCamTarget = planetPosition;
                        cutsceneCamZoom = CustomLerp(85.0f, 45.0f, p);
                    }
                    else
                    {
                        cutsceneCamPos = new Vector3(planetPosition.X, planetPosition.Y + 20.0f, planetPosition.Z + 120.0f);
                        cutsceneCamTarget = planetPosition;
                        cutsceneCamZoom = 45.0f;
                    }

                    if (cutsceneTimer >= CUTSCENE_DURATION)
                    {
                        currentState = GameState.Landing;
                        planetActive = false;
                        nextPlanetScore = score + 15000 + (planetLevel * 5000);
                        planetLevel++;
                        currentPlanetSize += 25.0f;
                        Raylib.StopSound(droneSound);
                    }
                }
                else if (currentState == GameState.Landing)
                {
                    if (Raylib.IsKeyPressed(KeyboardKey.Enter))
                    {
                        upgradeTokens++;
                        currentState = GameState.Upgrading;
                    }
                }
                else if (currentState == GameState.Upgrading)
                {
                    if (upgradeTokens > 0)
                    {
                        if (Raylib.IsKeyPressed(KeyboardKey.A)) { shipArmor = Math.Min(shipArmor + 50, 100); upgradeTokens--; }
                        if (Raylib.IsKeyPressed(KeyboardKey.L)) { shipLengthModifier += 1.0f; upgradeTokens--; }
                        if (Raylib.IsKeyPressed(KeyboardKey.B)) { nitroLevel++; maxNitro += 50.0f; upgradeTokens--; }
                        if (Raylib.IsKeyPressed(KeyboardKey.R)) { hasRamp = true; upgradeTokens--; }
                    }
                    if (Raylib.IsKeyPressed(KeyboardKey.Space))
                    {
                        currentState = GameState.Flying;
                        currentNitro = maxNitro;
                        ResetObstacles();
                    }
                }
                else if (currentState == GameState.GameOver)
                {
                    if (Raylib.IsKeyPressed(KeyboardKey.R))
                    {
                        shipPosition = new Vector3(0.0f, 0.0f, 20.0f);
                        survivalTime = 0.0f; score = 0; killStreak = 0; shipArmor = 100.0f;
                        shipLengthModifier = 0.0f; hasRamp = false; upgradeTokens = 0;
                        maxNitro = 100.0f; currentNitro = 100.0f; nitroLevel = 1;
                        planetLevel = 1; currentPlanetSize = 60.0f;
                        nextPlanetScore = 10000; nextBossScore = 25000; planetActive = false;
                        currentState = GameState.Flying;
                        ResetObstacles();
                    }
                }

                // ==========================================
                // RENDER PIPELINE (SHADOWED LAYER CANVAS)
                // ==========================================
                Camera3D camera = (currentState == GameState.PlanetApproach)
                    ? new Camera3D(cutsceneCamPos, cutsceneCamTarget, Vector3.UnitY, cutsceneCamZoom, CameraProjection.Perspective)
                    : new Camera3D(new Vector3(shipPosition.X * 0.85f, shipPosition.Y * 0.85f + 4.5f, shipPosition.Z + 12.0f), new Vector3(shipPosition.X, shipPosition.Y, shipPosition.Z - 20.0f), Vector3.UnitY, currentFOV, CameraProjection.Perspective);

                // Update dynamic variables for Shader calculations
                Raylib.SetShaderValue(screenShader, timeLoc, totalElapsedGameTime, ShaderUniformDataType.Float);

                // --- PASS 1: DRAW TO OFF-SCREEN CANVAS ---
                Raylib.BeginTextureMode(targetCanvas);
                Raylib.ClearBackground(new Color(3, 3, 8, 255)); // Slightly darker ambient backdrop

                if (currentState != GameState.Upgrading)
                {
                    Raylib.BeginMode3D(camera);

                    // Render Stars
                    for (int i = 0; i < maxStars; i++)
                    {
                        float starSpeedFactor = isBoosting ? 2.8f : 1.0f;
                        spaceDust[i].Position.Z += (forwardSpeed * 0.12f * spaceDust[i].SpeedModifier * (starSpeedFactor - 1.0f)) * deltaTime;

                        if (spaceDust[i].Position.Z > shipPosition.Z + 10.0f)
                        {
                            spaceDust[i].Position.Z = shipPosition.Z - 220.0f;
                            spaceDust[i].Position.X = (float)rand.NextDouble() * 110.0f - 55.0f;
                            spaceDust[i].Position.Y = (float)rand.NextDouble() * 70.0f - 35.0f;
                        }
                        Vector3 starEnd = spaceDust[i].Position + new Vector3(0, 0, isBoosting ? 15.0f : 4.0f);
                        Raylib.DrawLine3D(spaceDust[i].Position, starEnd, isBoosting ? Color.SkyBlue : Color.DarkBlue);
                    }

                    // Render Particles
                    for (int i = 0; i < maxParticles; i++)
                    {
                        if (particles[i].Active)
                            Raylib.DrawCube(particles[i].Position, 0.4f, 0.4f, 0.4f, particles[i].MainColor);
                    }

                    // Render Plasma Lasers
                    for (int i = 0; i < maxLasers; i++)
                    {
                        if (lasers[i].Active)
                        {
                            Raylib.DrawCube(lasers[i].Position, 0.15f, 0.15f, 3.0f, Color.Green);
                            Raylib.DrawCubeWires(lasers[i].Position, 0.2f, 0.2f, 3.1f, Color.Lime);
                        }
                    }

                    // Render Boss Dreadnought
                    if (currentState == GameState.BossFight)
                    {
                        Raylib.DrawCube(bossPosition, 18.0f, 4.5f, 22.0f, Color.Maroon);
                        Raylib.DrawCubeWires(bossPosition, 18.2f, 4.7f, 22.2f, Color.Red);
                        Raylib.DrawCube(bossPosition + new Vector3(0, 0, -11.5f), 6.0f, 1.5f, 1.0f, Color.Magenta);

                        if (bossLaserActive)
                        {
                            Raylib.DrawCube(bossLaserPos, 0.8f, 0.8f, 5.0f, Color.Purple);
                            Raylib.DrawLine3D(bossPosition, bossLaserPos, Color.Magenta);
                        }
                    }

                    // Planet
                    if (planetActive || currentState == GameState.PlanetApproach || currentState == GameState.Landing)
                    {
                        Raylib.DrawSphere(planetPosition, currentPlanetSize, Color.DarkPurple);
                        Raylib.DrawSphereWires(planetPosition, currentPlanetSize + 2.0f, 16, 16, Color.Magenta);
                    }

                    // Obstacles
                    for (int i = 0; i < maxObstacles; i++)
                    {
                        Raylib.DrawSphere(obstacles[i].Position, obstacles[i].Radius, obstacles[i].MainColor);
                        Raylib.DrawSphereWires(obstacles[i].Position, obstacles[i].Radius, 6, 6, obstacles[i].WireColor);
                    }

                    // Ship
                    if (currentState != GameState.GameOver)
                    {
                        float enginePulse = 1.0f + MathF.Sin((float)Raylib.GetTime() * 45.0f) * 0.15f;
                        Vector3 exhaustPos = shipPosition + new Vector3(0, 0, 1.4f + (shipLengthModifier / 2));
                        
                        if (isBoosting)
                        {
                            Raylib.DrawCube(exhaustPos, 0.7f * enginePulse, 0.7f * enginePulse, 4.5f, Color.SkyBlue);
                            Raylib.DrawCubeWires(exhaustPos, 0.75f * enginePulse, 0.75f * enginePulse, 4.6f, Color.Blue);
                        }
                        else
                        {
                            Raylib.DrawCube(exhaustPos, 0.5f * enginePulse, 0.5f * enginePulse, 1.0f, Color.Orange);
                        }

                        Rlgl.PushMatrix();
                        Rlgl.Translatef(shipPosition.X, shipPosition.Y, shipPosition.Z);
                        Rlgl.Rotatef(shipRoll,  0, 0, 1);
                        Rlgl.Rotatef(shipPitch, 1, 0, 0);
                        Raylib.DrawCube(Vector3.Zero, 1.2f, 0.6f, 2.8f + shipLengthModifier, Color.DarkBlue);
                        Raylib.DrawCubeWires(Vector3.Zero, 1.22f, 0.62f, 2.82f + shipLengthModifier, Color.SkyBlue);
                        Raylib.DrawCube(Vector3.Zero, 4.5f, 0.15f, 0.8f, Color.Blue);
                        if (hasRamp) Raylib.DrawCube(new Vector3(0, -0.4f, -1.5f), 1.0f, 0.2f, 1.5f, Color.Gray);
                        Rlgl.PopMatrix();
                    }

                    Raylib.EndMode3D();
                }

                // Render overlay elements onto texture frame buffer
                if (currentState == GameState.Flying || currentState == GameState.BossFight)
                {
                    Raylib.DrawRectangleGradientH(0, 0, screenWidth / 2, 60, new Color(0, 40, 80, 120), new Color(0, 0, 0, 0));
                    Raylib.DrawFPS(20, 15);
                    Raylib.DrawText($"SCORE: {score:D7}", 110, 15, 24, Color.Green);

                    if (killStreak > 0)
                        Raylib.DrawText($"STREAK: x{killStreak} ({streakTimer:F1}s)", screenWidth - 240, 15, 20, Color.Gold);

                    Color armorColor = shipArmor > 40 ? Color.Green : Color.Red;
                    Raylib.DrawText($"HULL: {shipArmor}%", 20, 60, 20, armorColor);

                    float nitroRatio = currentNitro / maxNitro;
                    Raylib.DrawText("NITRO:", 180, 60, 20, Color.LightGray);
                    Raylib.DrawRectangle(260, 64, 140, 12, Color.DarkGray);
                    Raylib.DrawRectangle(260, 64, (int)(140 * nitroRatio), 12, isBoosting ? Color.SkyBlue : Color.Blue);

                    if (currentState == GameState.BossFight)
                    {
                        Raylib.DrawRectangle(screenWidth / 2 - 200, 20, 400, 20, Color.Maroon);
                        Raylib.DrawRectangle(screenWidth / 2 - 200, 20, (int)(400 * (bossHealth / bossMaxHealth)), 20, Color.Red);
                        Raylib.DrawRectangleLines(screenWidth / 2 - 200, 20, 400, 20, Color.White);
                        Raylib.DrawText("BOSS: VOID BREAKER", screenWidth / 2 - 95, 23, 18, Color.White);
                    }
                }
                else if (currentState == GameState.Upgrading)
                {
                    Raylib.DrawRectangle(0, 0, screenWidth, screenHeight, new Color(15, 15, 30, 255));
                    Raylib.DrawText("--- PLANETARY OUTPOST OUTFITTERS ---", screenWidth / 2 - 280, 100, 30, Color.Gold);
                    Raylib.DrawText($"TOKENS AVAILABLE: {upgradeTokens}", screenWidth / 2 - 120, 170, 22, Color.White);

                    Raylib.DrawText("[A] INJECT NANO-REPAIR (+50% Hull)", screenWidth / 2 - 180, 240, 20, upgradeTokens > 0 ? Color.Green : Color.DarkGray);
                    Raylib.DrawText("[L] LENGTHEN CHASSIS STABILITY", screenWidth / 2 - 180, 290, 20, upgradeTokens > 0 ? Color.Green : Color.DarkGray);
                    Raylib.DrawText($"[B] UPGRADE NITRO CORE (LVL {nitroLevel})", screenWidth / 2 - 180, 340, 20, upgradeTokens > 0 ? Color.SkyBlue : Color.DarkGray);
                    Raylib.DrawText(hasRamp ? "[R] DEFLECTOR INSTALLED" : "[R] ADD TITANIUM DEFLECTOR RAMP", screenWidth / 2 - 180, 390, 20, (upgradeTokens > 0 && !hasRamp) ? Color.Green : Color.DarkGray);

                    Raylib.DrawText("PRESS [SPACE] TO LAUNCH SHIP", screenWidth / 2 - 150, 540, 20, Color.SkyBlue);
                }
                else if (currentState == GameState.Landing)
                {
                    Raylib.DrawRectangle(0, 0, screenWidth, screenHeight, new Color(0, 0, 0, 160));
                    Raylib.DrawText("SAFE DOCKING AREA CLEAR", screenWidth / 2 - 240, screenHeight / 2 - 30, 36, Color.Magenta);
                    Raylib.DrawText("PRESS 'ENTER' TO TOUCH DOWN", screenWidth / 2 - 150, screenHeight / 2 + 30, 20, Color.White);
                }
                else if (currentState == GameState.GameOver)
                {
                    Raylib.DrawRectangle(0, 0, screenWidth, screenHeight, new Color(40, 0, 0, 220));
                    Raylib.DrawText("CRITICAL FAILURE - SHIP DESTROYED", screenWidth / 2 - 340, screenHeight / 2 - 40, 36, Color.Red);
                    Raylib.DrawText("PRESS 'R' TO RESPAWN CHASSIS", screenWidth / 2 - 150, screenHeight / 2 + 30, 20, Color.Gray);
                }

                Raylib.EndTextureMode();

                // --- PASS 2: RENDER CANVAS TO WINDOW THROUGH THE SHADER ---
                Raylib.BeginDrawing();
                Raylib.ClearBackground(Color.Black);

                Raylib.BeginShaderMode(screenShader);
                // Draw target canvas flipped along Y-axis because textures load upside down in modern OpenGL framing
                Raylib.DrawTextureRec(
                    targetCanvas.Texture, 
                    new Rectangle(0, 0, targetCanvas.Texture.Width, -targetCanvas.Texture.Height), 
                    Vector2.Zero, 
                    Color.White
                );
                Raylib.EndShaderMode();

                Raylib.EndDrawing();
            }

            // --- CLEANUP ---
            Raylib.UnloadShader(screenShader);
            Raylib.UnloadRenderTexture(targetCanvas);
            Raylib.UnloadSound(rumbleSound); Raylib.UnloadSound(stingSound); Raylib.UnloadSound(droneSound);
            Raylib.UnloadSound(laserSound); Raylib.UnloadSound(explodeSound);
            Raylib.CloseAudioDevice();
            Raylib.CloseWindow();
        }
    }
}