// Pedal Gain Multi — panel support classes (v2.0.0)
//
// Two pieces of the parameter panel that carry real correctness
// requirements, kept apart from the WPF layout code so they can be
// tested headlessly:
//
//   FaderTaper     — the console-style dB code ↔ slider-travel mapping used
//                    by the VOL faders and the master fader. Every one of the
//                    182 codes must round-trip value → travel → value.
//
//   MeterBallistics— IEC-style meter decay, peak hold and clip latching,
//                    timed by a real clock rather than a frame count so the
//                    fall rate is honest when frames are dropped.

using System;

namespace WDE.PedalGainMulti
{
    // ── Fader taper ──────────────────────────────────────────────────────────
    //
    // A linear dB fader wastes most of its travel on levels nobody uses: half
    // the throw would sit below -40 dB. A console taper gives the useful
    // -20..+10 dB region most of the space.
    //
    // The mapping is piecewise linear between named (dB, travel) points, and
    // travel below 0.01 snaps to code 0 (-inf) so the bottom of the throw is
    // a real mute rather than -80 dB.
    public static class FaderTaper
    {
        // dB → travel breakpoints, from the handoff.
        static readonly float[] Db     = { -80f, -60f, -40f, -30f, -20f, -10f, -5f,  0f,   5f,  10f  };
        static readonly float[] Travel = { 0.02f, 0.08f, 0.20f, 0.30f, 0.42f, 0.60f, 0.68f, 0.75f, 0.87f, 1.00f };

        // Below this travel the fader reads as -inf (code 0).
        const float MuteTravel = 0.01f;

        /// dB code (0..181) → slider travel (0..1).
        public static float CodeToTravel(int code)
        {
            if (code <= 0) return 0f;
            float db = (code - 161) * 0.5f;
            return DbToTravel(db);
        }

        /// Slider travel (0..1) → dB code (0..181).
        public static int TravelToCode(float travel)
        {
            if (travel < MuteTravel) return 0;
            float db = TravelToDb(travel);
            int code = (int)MathF.Round(db * 2f) + 161;
            return Math.Clamp(code, 1, 181);
        }

        public static float DbToTravel(float db)
        {
            if (db <= Db[0])              return Travel[0];
            if (db >= Db[Db.Length - 1])  return Travel[Travel.Length - 1];
            for (int i = 1; i < Db.Length; i++)
            {
                if (db > Db[i]) continue;
                float span = Db[i] - Db[i - 1];
                float t    = span > 0f ? (db - Db[i - 1]) / span : 0f;
                return Travel[i - 1] + t * (Travel[i] - Travel[i - 1]);
            }
            return Travel[Travel.Length - 1];
        }

        public static float TravelToDb(float travel)
        {
            if (travel <= Travel[0])                  return Db[0];
            if (travel >= Travel[Travel.Length - 1])  return Db[Db.Length - 1];
            for (int i = 1; i < Travel.Length; i++)
            {
                if (travel > Travel[i]) continue;
                float span = Travel[i] - Travel[i - 1];
                float t    = span > 0f ? (travel - Travel[i - 1]) / span : 0f;
                return Db[i - 1] + t * (Db[i] - Db[i - 1]);
            }
            return Db[Db.Length - 1];
        }
    }

    // ── Meter ballistics ─────────────────────────────────────────────────────
    //
    // One instance per meter bar. Fed the exact peak since the last read
    // (the machine's accumulator guarantees no peak is missed between polls),
    // and produces the displayed level, the held peak and the clip state.
    //
    // Timing comes from elapsed seconds passed in by the caller, not from a
    // frame count: at 60 fps a frame-count decay looks right, but when the UI
    // thread stalls the meter would fall in slow motion. Passing a real
    // Stopwatch delta keeps the fall rate honest.
    public sealed class MeterBallistics
    {
        // IEC 60268-18 style: 20 dB of fall in 1.7 s ≈ 11.76 dB/s.
        const float FallDbPerSecond = 20f / 1.7f;
        // Peak hold before the held value starts falling at the same rate.
        const float HoldSeconds     = 3.0f;
        // Clip light stays lit this long after the last sample at/over 0 dBFS.
        const float ClipHoldSeconds = 2.0f;
        // Floor for the display.
        public const float MinDb = -60f;

        /// Current displayed level in dBFS (instant attack, timed fall).
        public float LevelDb { get; private set; } = MinDb;

        /// Held peak in dBFS — what the numeric readout shows.
        public float HoldDb { get; private set; } = MinDb;

        /// True while the clip indicator should be lit.
        public bool Clipped { get; private set; }

        float holdTimer;      // seconds since the held peak was last refreshed
        float clipTimer;      // seconds since the last over

        /// Feed one frame.
        ///   peakLinear — exact peak since the last read, 0..1 full scale
        ///   dt         — seconds since the previous Update
        public void Update(float peakLinear, float dt)
        {
            if (dt < 0f) dt = 0f;

            float db = peakLinear > 1e-7f ? 20f * MathF.Log10(peakLinear) : MinDb;
            if (db < MinDb) db = MinDb;

            // Instant attack, timed fall.
            if (db >= LevelDb) LevelDb = db;
            else               LevelDb = MathF.Max(LevelDb - FallDbPerSecond * dt, MinDb);

            // Peak hold: refresh on a new high, otherwise hold then fall.
            if (db >= HoldDb)
            {
                HoldDb    = db;
                holdTimer = 0f;
            }
            else
            {
                holdTimer += dt;
                if (holdTimer > HoldSeconds)
                    HoldDb = MathF.Max(HoldDb - FallDbPerSecond * dt, MinDb);
            }

            // Clip latch: any sample at or above 0 dBFS lights it; it stays
            // lit for ClipHoldSeconds after the last one.
            if (db >= 0f)
            {
                Clipped   = true;
                clipTimer = 0f;
            }
            else if (Clipped)
            {
                clipTimer += dt;
                if (clipTimer > ClipHoldSeconds) Clipped = false;
            }
        }

        /// Clear hold and clip — the "click any meter to reset" action.
        public void ClearHold()
        {
            HoldDb    = LevelDb;
            holdTimer = 0f;
            Clipped   = false;
            clipTimer = 0f;
        }

        /// Reset everything, e.g. when the panel reopens.
        public void Reset()
        {
            LevelDb   = MinDb;
            HoldDb    = MinDb;
            Clipped   = false;
            holdTimer = 0f;
            clipTimer = 0f;
        }
    }
}
