namespace PluginBuilder.Extension
{
    public static class FileExtensions
    {
        private static readonly string[] _permittedExtensions = { ".jpg", ".jpeg", ".png", ".gif" };
        private const long _maxFileSize = 1024 * 1024;

        public static bool ValidateUploadedImage(this IFormFile file, out string error, long maxFileSizeInBytes = 1_000_000)
        {
            if (file == null || file.Length == 0)
            {
                error = "No file was uploaded or the file is empty.";
                return false;
            }
            if (!file.FileName.IsFileValidImage())
            {
                error = "Could not complete file upload. File has invalid name";
                return false;
            }
            if (!file.FileName.IsValidFileName())
            {
                error = "Invalid file type. Only images are allowed";
                return false;
            }
            if (file.Length > maxFileSizeInBytes)
            {
                error = $"The uploaded image file should be less than {maxFileSizeInBytes / 1_000_000}MB";
                return false;
            }
            if (!file.ContentType.StartsWith("image/", StringComparison.InvariantCulture))
            {
                error = "The uploaded file needs to be an image (based on content type)";
                return false;
            }
            error = null;
            return true;
        }

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
