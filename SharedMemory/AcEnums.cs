namespace AvoTelemetryAgent.SharedMemory;

public enum AC_STATUS
{
    AC_OFF = 0,
    AC_REPLAY = 1,
    AC_LIVE = 2,
    AC_PAUSE = 3,
}

public enum AC_SESSION_TYPE
{
    AC_UNKNOWN = -1,
    AC_PRACTICE = 0,
    AC_QUALIFY = 1,
    AC_RACE = 2,
    AC_HOTLAP = 3,
    AC_TIME_ATTACK = 4,
    AC_DRIFT = 5,
    AC_DRAG = 6,
}

public enum AC_FLAG_TYPE
{
    AC_NO_FLAG = 0,
    AC_BLUE_FLAG = 1,
    AC_YELLOW_FLAG = 2,
    AC_BLACK_FLAG = 3,
    AC_WHITE_FLAG = 4,
    AC_CHECKERED_FLAG = 5,
    AC_PENALTY_FLAG = 6,
    AC_GREEN_FLAG = 7,
    AC_ORANGE_FLAG = 8,
}

public enum AC_PENALTY_SHORTCUT
{
    None = 0,
    DriveThrough_Cutting = 1,
    StopAndGo_10_Cutting = 2,
    StopAndGo_20_Cutting = 3,
    StopAndGo_30_Cutting = 4,
    Disqualified_Cutting = 5,
    RemoveBestLaptime_Cutting = 6,
    DriveThrough_PitSpeeding = 7,
    StopAndGo_10_PitSpeeding = 8,
    StopAndGo_20_PitSpeeding = 9,
    StopAndGo_30_PitSpeeding = 10,
    Disqualified_PitSpeeding = 11,
    RemoveBestLaptime_PitSpeeding = 12,
    Disqualified_IgnoredMandatoryPit = 13,
    PostRaceTime = 14,
    Disqualified_Trolling = 15,
    Disqualified_PitEntry = 16,
    Disqualified_PitExit = 17,
    Disqualified_WrongWay = 18,
    DriveThrough_IgnoredDriverStint = 19,
    Disqualified_IgnoredDriverStint = 20,
    Disqualified_ExceededDriverStintLimit = 21,
}

public enum AC_TRACK_GRIP_STATUS
{
    Green = 0,
    Fast = 1,
    Optimum = 2,
    Greasy = 3,
    Damp = 4,
    Wet = 5,
    Flooded = 6,
}

public enum AC_RAIN_INTENSITY
{
    No_Rain = 0,
    Drizzle = 1,
    Light_Rain = 2,
    Medium_Rain = 3,
    Heavy_Rain = 4,
    Thunderstorm = 5,
}
