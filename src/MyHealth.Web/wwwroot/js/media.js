// Small browser helpers for media uploads.
window.myhealthMedia = {
    // Length in seconds of the video chosen in a file input, read from its metadata without uploading it.
    videoDuration: function (inputId) {
        return new Promise(function (resolve) {
            var input = document.getElementById(inputId);
            var file = input && input.files && input.files[0];
            if (!file) {
                resolve(0);
                return;
            }

            var url = URL.createObjectURL(file);
            var video = document.createElement('video');
            video.preload = 'metadata';
            video.onloadedmetadata = function () {
                URL.revokeObjectURL(url);
                resolve(isFinite(video.duration) ? video.duration : 0);
            };
            video.onerror = function () {
                URL.revokeObjectURL(url);
                resolve(0);
            };
            video.src = url;
        });
    }
};

// Installable app (PWA): the service worker only serves the offline page when there is no connection.
if ('serviceWorker' in navigator && location.protocol === 'https:') {
    navigator.serviceWorker.register('/sw.js').catch(() => { });
}
