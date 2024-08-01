namespace PluginBuilder.Extension
{
    public static class FileExtensions
    {
        private static readonly string[] _permittedExtensions = { ".jpg", ".jpeg", ".png", ".gif" };
        private const long _maxFileSize = 1024 * 1024;
        public static bool IsValidFileName(this string fileName)
        {
            return !fileName.ToCharArray().Any(c => Path.GetInvalidFileNameChars().Contains(c)
                                                    || c == Path.AltDirectorySeparatorChar
                                                    || c == Path.DirectorySeparatorChar
                                                    || c == Path.PathSeparator
                                                    || c == '\\');
        }

        public static bool IsFileValidImage(this string fileName)
        {
            var ext = Path.GetExtension(fileName)?.ToLowerInvariant();
            return !string.IsNullOrEmpty(ext) && _permittedExtensions.Contains(ext);
        }
        public static bool IsImageValidSize(this long imageLength)
        {
            return imageLength <= _maxFileSize;
        }
    }
}
