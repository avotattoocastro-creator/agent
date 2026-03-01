using System.Runtime.InteropServices;

namespace AvoTelemetryAgent.SharedMemory;

/// <summary>
/// Maps exactly to the "acpmf_static" shared-memory page.
/// Fields are in the original Assetto Corsa SDK order (StructLayout Sequential, Pack=1).
/// String fields use fixed char (UTF-16, matching AC wchar_t).
/// Layout is preserved in full up to the last field we use (WetTyresName).
/// </summary>
[StructLayout(LayoutKind.Sequential, Pack = 1)]
public unsafe struct SPageFileStatic
{
    public fixed char   SmVersion[15];          // offset   0  (30 bytes)
    public fixed char   AcVersion[15];          // offset  30  (30 bytes)
    public int          NumberOfSessions;       // offset  60
    public int          NumCars;                // offset  64
    public fixed char   CarModel[33];           // offset  68  (66 bytes)
    public fixed char   Track[33];              // offset 134  (66 bytes)
    public fixed char   PlayerName[33];         // offset 200  (66 bytes)
    public fixed char   PlayerSurname[33];      // offset 266  (66 bytes)
    public fixed char   PlayerNick[33];         // offset 332  (66 bytes)
    public int          SectorCount;            // offset 398
    public float        MaxTorque;              // offset 402
    public float        MaxPower;               // offset 406
    public float        MaxRpm;                 // offset 410
    public float        MaxFuel;                // offset 414
    public fixed float  SuspensionMaxTravel[4]; // offset 418  (16 bytes)
    public fixed float  TyreRadius[4];          // offset 434  (16 bytes)
    public float        MaxTurboBoost;          // offset 450
    public float        Deprecated_AirTemp;     // offset 454
    public float        Deprecated_RoadTemp;    // offset 458
    public int          PenaltiesEnabled;       // offset 462
    public float        AidFuelRate;            // offset 466
    public float        AidTireRate;            // offset 470
    public float        AidMechanicalDamage;    // offset 474
    public int          AidAllowTyreBlankets;   // offset 478
    public float        AidStability;           // offset 482
    public int          AidAutoclutch;          // offset 486
    public int          AidAutoBlip;            // offset 490
    public int          HasDRS;                 // offset 494
    public int          HasERS;                 // offset 498
    public int          HasKERS;                // offset 502
    public float        KersMaxJ;               // offset 506
    public int          EngineBrakeSettingsCount; // offset 510
    public int          ErsPowerControllerCount; // offset 514
    public float        TrackSPlineLength;      // offset 518
    public fixed char   TrackConfiguration[33]; // offset 522  (66 bytes)
    public float        ErsMaxJ;                // offset 588
    public int          IsTimedRace;            // offset 592
    public int          HasExtraLap;            // offset 596
    public fixed char   CarSkin[33];            // offset 600  (66 bytes)
    public int          ReversedGridPositions;  // offset 666
    public int          PitWindowStart;         // offset 670
    public int          PitWindowEnd;           // offset 674
    public int          IsOnline;               // offset 678
    public fixed char   DryTyresName[33];       // offset 682  (66 bytes)
    public fixed char   WetTyresName[33];       // offset 748  (66 bytes)
    // Total used: 814 bytes
}
