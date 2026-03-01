using System.Runtime.InteropServices;

namespace AvoTelemetryAgent.SharedMemory;

/// <summary>
/// Maps exactly to the "acpmf_graphics" shared-memory page.
/// Fields are in the original Assetto Corsa SDK order (StructLayout Sequential, Pack=1).
/// String fields use fixed char (UTF-16, matching AC wchar_t).
/// Layout is preserved in full up to the last field we use (CurrentTyreSet).
/// </summary>
[StructLayout(LayoutKind.Sequential, Pack = 1)]
public unsafe struct SPageFileGraphics
{
    public int              PacketId;               // offset    0
    public AC_STATUS        Status;                 // offset    4  (int)
    public AC_SESSION_TYPE  Session;                // offset    8  (int)
    public fixed char       CurrentTime[15];        // offset   12  (30 bytes, wchar_t[15])
    public fixed char       LastTime[15];           // offset   42  (30 bytes)
    public fixed char       BestTime[15];           // offset   72  (30 bytes)
    public fixed char       Split[15];              // offset  102  (30 bytes)
    public int              CompletedLaps;          // offset  132
    public int              Position;               // offset  136
    public int              ICurrentTime;           // offset  140  ms
    public int              ILastTime;              // offset  144  ms
    public int              IBestTime;              // offset  148  ms
    public float            SessionTimeLeft;        // offset  152
    public float            DistanceTraveled;       // offset  156
    public int              IsInPit;                // offset  160
    public int              CurrentSectorIndex;     // offset  164
    public int              LastSectorTime;         // offset  168
    public int              NumberOfLaps;           // offset  172
    public fixed char       TyreCompound[33];       // offset  176  (66 bytes, wchar_t[33])
    public float            ReplayTimeMultiplier;   // offset  242
    public float            NormalizedCarPosition;  // offset  246
    public int              ActiveCars;             // offset  250
    public fixed float      CarCoordinates[180];    // offset  254  [60][3] = 720 bytes
    public fixed int        CarID[60];              // offset  974  (240 bytes)
    public int              PlayerCarID;            // offset 1214
    public float            PenaltyTime;            // offset 1218
    public AC_FLAG_TYPE     Flag;                   // offset 1222  (int)
    public AC_PENALTY_SHORTCUT Penalty;             // offset 1226  (int)
    public int              IdealLineOn;            // offset 1230
    public int              IsInPitLane;            // offset 1234
    public float            SurfaceGrip;            // offset 1238
    public int              MandatoryPitDone;       // offset 1242
    public float            WindSpeed;              // offset 1246
    public float            WindDirection;          // offset 1250
    public int              IsSetupMenuVisible;     // offset 1254
    public int              MainDisplayIndex;       // offset 1258
    public int              SecondaryDisplayIndex;  // offset 1262
    public int              TC;                     // offset 1266
    public int              TCCut;                  // offset 1270
    public int              EngineMap;              // offset 1274
    public int              ABS;                    // offset 1278
    public int              FuelXLap;               // offset 1282
    public int              RainLights;             // offset 1286
    public int              FlashingLights;         // offset 1290
    public int              LightsStage;            // offset 1294
    public float            ExhaustTemperature;     // offset 1298
    public int              WiperLV;                // offset 1302
    public int              DriverStintTotalTimeLeft; // offset 1306
    public int              DriverStintTimeLeft;    // offset 1310
    public int              RainTyres;              // offset 1314
    public int              SessionIndex;           // offset 1318
    public float            UsedFuel;               // offset 1322
    public fixed char       DeltaLapTime[15];       // offset 1326  (30 bytes)
    public int              IDeltaLapTime;          // offset 1356
    public fixed char       EstimatedLapTime[15];   // offset 1360  (30 bytes)
    public int              IEstimatedLapTime;      // offset 1390
    public int              IsDeltaPositive;        // offset 1394
    public int              ISplit;                 // offset 1398
    public int              IsValidLap;             // offset 1402
    public float            FuelEstimatedLaps;      // offset 1406
    public fixed char       TrackStatus[33];        // offset 1410  (66 bytes)
    public int              MissingMandatoryPits;   // offset 1476
    public float            Clock;                  // offset 1480
    public int              DirectionLightsLeft;    // offset 1484
    public int              DirectionLightsRight;   // offset 1488
    public int              GlobalYellow;           // offset 1492
    public int              GlobalYellow1;          // offset 1496
    public int              GlobalYellow2;          // offset 1500
    public int              GlobalYellow3;          // offset 1504
    public int              GlobalWhite;            // offset 1508
    public int              GlobalGreen;            // offset 1512
    public int              GlobalChequered;        // offset 1516
    public int              GlobalRed;              // offset 1520
    public int              MfdTyreSet;             // offset 1524
    public float            MfdFuelToAdd;           // offset 1528
    public float            MfdTyrePressureLF;      // offset 1532
    public float            MfdTyrePressureRF;      // offset 1536
    public float            MfdTyrePressureLR;      // offset 1540
    public float            MfdTyrePressureRR;      // offset 1544
    public AC_TRACK_GRIP_STATUS TrackGripStatus;    // offset 1548  (int)
    public AC_RAIN_INTENSITY    RainIntensity;      // offset 1552  (int)
    public AC_RAIN_INTENSITY    RainIntensityIn10min; // offset 1556  (int)
    public AC_RAIN_INTENSITY    RainIntensityIn30min; // offset 1560  (int)
    public int              CurrentTyreSet;         // offset 1564
    // Total used: 1568 bytes
}
