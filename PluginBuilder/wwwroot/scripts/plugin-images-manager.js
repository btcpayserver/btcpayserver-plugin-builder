(() => {
    const DEFAULT_MAX_IMAGES = 10;

    function init(options) {
        const {
            listId,
            inputId,
            limitErrorId,
            modalId,
            modalImageId,
            maxImages = DEFAULT_MAX_IMAGES
        } = options || {};

        const list = document.getElementById(listId);
        const input = document.getElementById(inputId);
        const limitError = document.getElementById(limitErrorId);
        const modalElement = document.getElementById(modalId);
        const modalImage = document.getElementById(modalImageId);

        const getModal = () => {
            if (!modalElement || !window.bootstrap?.Modal)
                return null;

            return window.bootstrap.Modal.getOrCreateInstance(modalElement);
        };

        if (!list || !input) {
            return;
        }

        let newImageIndex = 0;
        const newImages = new Map();
        let draggedItem = null;

        const disableNativeImageDrag = (scope) => {
            Array.from((scope || list).querySelectorAll('.image-preview')).forEach(img => {
                img.setAttribute('draggable', 'false');
            });
        };

        const setLimitMessage = (message) => {
            if (!limitError)
                return;

            if (message) {
                limitError.textContent = message;
                limitError.classList.remove('d-none');
            }
            else {
                limitError.textContent = '';
                limitError.classList.add('d-none');
            }
        };

        const rebuildInputFileList = () => {
            const dataTransfer = new DataTransfer();
            Array.from(list.querySelectorAll('[data-image-item][data-new-id]')).forEach(entry => {
                const id = entry.getAttribute('data-new-id');
                const image = id ? newImages.get(id) : null;
                if (image)
                    dataTransfer.items.add(image.file);
            });
            input.files = dataTransfer.files;
        };

        input.addEventListener('change', () => {
            const selectedFiles = Array.from(input.files || []);
            const currentCount = list.querySelectorAll('[data-image-item]').length;
            const remaining = Math.max(maxImages - currentCount, 0);
            const acceptedFiles = selectedFiles.slice(0, remaining);
            const droppedCount = selectedFiles.length - acceptedFiles.length;

            for (let i = acceptedFiles.length - 1; i >= 0; i--) {
                const file = acceptedFiles[i];
                const imageId = `new-${newImageIndex++}`;
                const previewUrl = URL.createObjectURL(file);
                newImages.set(imageId, { file, previewUrl });

                const card = document.createElement('div');
                card.className = 'image-item';
                card.setAttribute('data-image-item', 'true');
                card.setAttribute('draggable', 'true');
                card.setAttribute('data-new-id', imageId);
                card.innerHTML = `
                    <input type="hidden" name="ImagesOrder" value="new" data-order-input />
                    <button type="button" class="image-preview-btn" data-preview-trigger>
                        <img src="${previewUrl}" alt="Image" class="image-preview" draggable="false" />
                    </button>
                    <div class="image-actions">
                        <span class="text-muted small">Drag to reorder</span>
                        <button type="button" class="image-remove-action" data-remove-image aria-label="Remove image" title="Remove">
                            <span class="image-close-glyph" aria-hidden="true">&times;</span>
                        </button>
                    </div>`;
                list.prepend(card);
            }

            disableNativeImageDrag();

            if (droppedCount > 0) {
                setLimitMessage(`Maximum ${maxImages} images per plugin. ${droppedCount} file(s) were ignored.`);
            }
            else {
                setLimitMessage('');
            }

            input.value = '';
            rebuildInputFileList();
        });

        list.addEventListener('dragstart', evt => {
            const item = evt.target.closest('[data-image-item]');
            if (!item) {
                evt.preventDefault();
                return;
            }

            draggedItem = item;
            item.classList.add('is-dragging');
            if (evt.dataTransfer) {
                evt.dataTransfer.effectAllowed = 'move';
                evt.dataTransfer.setData('text/plain', 'image');
                const rect = item.getBoundingClientRect();
                evt.dataTransfer.setDragImage(item, rect.width / 2, rect.height / 2);
            }
        });

        list.addEventListener('dragover', evt => {
            if (!draggedItem)
                return;

            evt.preventDefault();
            const target = evt.target.closest('[data-image-item]');
            if (!target || target === draggedItem)
                return;

            const rect = target.getBoundingClientRect();
            const placeBefore = evt.clientY < rect.top + (rect.height / 2);
            list.insertBefore(draggedItem, placeBefore ? target : target.nextElementSibling);
        });

        list.addEventListener('drop', evt => {
            if (!draggedItem)
                return;

            evt.preventDefault();
            rebuildInputFileList();
        });

        list.addEventListener('dragend', () => {
            if (draggedItem) {
                draggedItem.classList.remove('is-dragging');
                draggedItem = null;
            }
            rebuildInputFileList();
        });

        list.addEventListener('click', evt => {
            const target = evt.target.closest('button');
            if (!target)
                return;

            const item = target.closest('[data-image-item]');
            if (!item)
                return;

            if (target.hasAttribute('data-preview-trigger')) {
                if (modalImage) {
                    const img = item.querySelector('.image-preview');
                    if (img) {
                        const modal = getModal();
                        if (!modal)
                            return;

                        modalImage.src = img.getAttribute('src') || '';
                        modalImage.alt = img.getAttribute('alt') || 'Image preview';
                        modal.show();
                    }
                }
                return;
            }

            if (target.hasAttribute('data-remove-image')) {
                const imageId = item.getAttribute('data-new-id');
                if (imageId && newImages.has(imageId)) {
                    URL.revokeObjectURL(newImages.get(imageId).previewUrl);
                    newImages.delete(imageId);
                }

                item.remove();
                rebuildInputFileList();
                setLimitMessage('');
            }
        });

        const existingCount = list.querySelectorAll('[data-image-item]').length;
        if (existingCount >= maxImages)
            setLimitMessage(`Maximum ${maxImages} images per plugin.`);

        disableNativeImageDrag();
    }

    window.PluginImagesManager = { init };
})();
