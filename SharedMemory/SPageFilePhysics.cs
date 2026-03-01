using System.Runtime.InteropServices;

namespace AvoTelemetryAgent.SharedMemory;

/// <summary>
/// Maps exactly to the "acpmf_physics" shared-memory page.
/// Fields are in the original Assetto Corsa SDK order (StructLayout Sequential, Pack=1).
/// Only fields required by AvoTelemetryAgent are named; the layout is preserved in full
/// up to the last field we use (LocalVelocity).
/// </summary>
[StructLayout(LayoutKind.Sequential, Pack = 1)]
public unsafe struct SPageFilePhysics
{
    public int     PacketId;            // offset   0
    public float   Gas;                 // offset   4
    public float   Brake;               // offset   8
    public float   Fuel;                // offset  12
    public int     Gear;                // offset  16
    public int     Rpms;                // offset  20
    public float   SteerAngle;          // offset  24
    public float   SpeedKmh;            // offset  28
    public fixed float Velocity[3];     // offset  32  (12 bytes)
    public fixed float AccG[3];         // offset  44  (12 bytes)
    public fixed float WheelSlip[4];    // offset  56  (16 bytes)
    public fixed float WheelLoad[4];    // offset  72  (16 bytes)
    public fixed float WheelsPressure[4]; // offset  88  (16 bytes)
    public fixed float WheelAngularSpeed[4]; // offset 104  (16 bytes)
    public fixed float TyreWear[4];     // offset 120  (16 bytes)
    public fixed float TyreDirtyLevel[4]; // offset 136  (16 bytes)
    public fixed float TyreCoreTempI[4]; // offset 152  (16 bytes)
    public fixed float CamberRAD[4];    // offset 168  (16 bytes)
    public fixed float SuspensionTravel[4]; // offset 184  (16 bytes)
    public float   Drs;                 // offset 200
    public float   Tc;                  // offset 204
    public float   Heading;             // offset 208
    public float   Pitch;               // offset 212
    public float   Roll;                // offset 216
    public float   CgHeight;            // offset 220
    public fixed float CarDamage[5];    // offset 224  (20 bytes)
    public int     NumberOfTyresOut;    // offset 244
    public int     PitLimiterOn;        // offset 248
    public float   Abs;                 // offset 252
    public float   KersCharge;          // offset 256
    public float   KersInput;           // offset 260
    public int     AutoShifterOn;       // offset 264
    public fixed float RideHeight[2];   // offset 268  ( 8 bytes)
    public float   TurboBoost;          // offset 276
    public float   Ballast;             // offset 280
    public float   AirDensity;          // offset 284
    public float   AirTemp;             // offset 288
    public float   RoadTemp;            // offset 292
    public fixed float LocalAngularVel[3]; // offset 296  (12 bytes)
    public float   FinalFF;             // offset 308
    public float   PerformanceMeter;    // offset 312
    public int     EngineBrake;         // offset 316
    public int     ErsRecoveryLevel;    // offset 320
    public int     ErsPowerLevel;       // offset 324
    public int     ErsHeatCharging;     // offset 328
    public int     ErsIsCharging;       // offset 332
    public float   KersCurrentKJ;       // offset 336
    public int     DrsAvailable;        // offset 340
    public int     DrsEnabled;          // offset 344
    public fixed float BrakeTemp[4];    // offset 348  (16 bytes)
    public float   Clutch;              // offset 364
    public fixed float TyreTempI[4];    // offset 368  (16 bytes)
    public fixed float TyreTempM[4];    // offset 384  (16 bytes)
    public fixed float TyreTempO[4];    // offset 400  (16 bytes)
    public int     IsAIControlled;      // offset 416
    public fixed float TyreContactPoint[12];   // offset 420  [4][3] = 48 bytes
    public fixed float TyreContactNormal[12];  // offset 468  [4][3] = 48 bytes
    public fixed float TyreContactHeading[12]; // offset 516  [4][3] = 48 bytes
    public float   BrakeBias;           // offset 564
    public fixed float LocalVelocity[3]; // offset 568  (12 bytes)
    // Total used: 580 bytes
}
