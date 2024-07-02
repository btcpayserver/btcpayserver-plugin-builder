document.addEventListener('DOMContentLoaded', function () {
    const dropArea = document.getElementById('drop-area');
    const fileInput = document.getElementById('fileInput');
    const previewContainer = document.getElementById('preview');

    fileInput.addEventListener('change', handleFiles);

    ['dragenter', 'dragover', 'dragleave', 'drop'].forEach(eventName => {
        dropArea.addEventListener(eventName, preventDefaults, false);
    });

    dropArea.addEventListener('dragover', () => dropArea.classList.add('bg-light'));
    dropArea.addEventListener('dragleave', () => dropArea.classList.remove('bg-light'));
    dropArea.addEventListener('drop', (e) => {
        dropArea.classList.remove('bg-light');
        const dt = e.dataTransfer;
        const files = dt.files;
        fileInput.files = files;
        handleFiles();
    });

    function preventDefaults(e) {
        e.preventDefault();
        e.stopPropagation();
    }

    function handleFiles() {
        const files = fileInput.files;
        if (files.length > 0) {
            previewFiles(files);
        } else {
            clearPreview();
        }
    }

    function previewFiles(files) {
        clearPreview();
        const file = files[0];
        const preview = document.createElement('div');
        preview.classList.add('file-preview');
        const fileName = document.createElement('span');
        fileName.classList.add('file-name');
        fileName.textContent = file.name;
        const deleteIcon = document.createElement('span');
        deleteIcon.classList.add('delete-icon');
        deleteIcon.innerHTML = '&times;';
        deleteIcon.addEventListener('click', () => {
            fileInput.value = '';
            clearPreview();
        });
        preview.appendChild(fileName);
        preview.appendChild(deleteIcon);
        previewContainer.appendChild(preview);
    }

    function clearPreview() {
        previewContainer.innerHTML = '';
    }
});
