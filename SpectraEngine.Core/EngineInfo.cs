namespace SpectraEngine.Core
{
    public static class EngineInfo
    {
        // const fields for versioning and other build-time engine info
        public const int MajorVersion = 1;
        public const int MinorVersion = 0;
        public const int RevisionVersion = 0;
        public static readonly string VersionString = $"{MajorVersion}.{MinorVersion}.{RevisionVersion}";

        // Internal engine versioning
        public const int ModelFormatVersion = 1;
        public const int MapFormatVersion = 1;
    }
}
