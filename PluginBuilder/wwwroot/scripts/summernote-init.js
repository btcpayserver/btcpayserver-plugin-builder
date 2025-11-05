(function () {
    document.addEventListener('DOMContentLoaded', function () {
        $('.richtext').summernote({
            height: 250,
            lang: 'en-US',
            toolbar: [
                ['style', ['bold', 'italic', 'underline', 'clear']],
                ['font', ['fontsize', 'color']],
                ['para', ['ul', 'ol', 'paragraph']],
                ['insert', ['link', 'picture']],
                ['view', ['fullscreen', 'codeview']]
            ]
        });
    });
})();
