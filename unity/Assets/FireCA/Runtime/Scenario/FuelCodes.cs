namespace FireCA
{
    /// <summary>The fuel codes of fuel.png. The codes match the LANDFIRE FBFM13 product.</summary>
    public static class FuelCodes
    {
        public const byte Urban = 91;
        public const byte SnowOrIce = 92;
        public const byte Agriculture = 93;
        public const byte Water = 98;
        public const byte Barren = 99;

        public const int FuelModelCount = 13;

        static readonly string[] ModelNames =
        {
            "Short grass",
            "Timber grass and understory",
            "Tall grass",
            "Chaparral",
            "Brush",
            "Dormant brush",
            "Southern rough",
            "Closed timber litter",
            "Hardwood litter",
            "Timber litter and understory",
            "Light slash",
            "Medium slash",
            "Heavy slash",
        };

        public static bool IsBurnable(int code) => code >= 1 && code <= FuelModelCount;

        public static string NameOf(int code)
        {
            if (IsBurnable(code)) return ModelNames[code - 1];
            switch (code)
            {
                case Urban: return "Urban";
                case SnowOrIce: return "Snow or ice";
                case Agriculture: return "Agriculture";
                case Water: return "Water";
                case Barren: return "Barren";
                default: return "No data";
            }
        }
    }
}
