(function () {
    'use strict';

    // Adds a "VR Share Link" button to the item detail page's button row
    // (admins only). Clicking it asks the plugin's own backend to mint a
    // time-limited jellyfin-vrc-stream share link and copies it to the
    // clipboard. Uses `.mainDetailButtons` (the row holding Play/More/etc.,
    // see src/apps/legacy/controllers/itemDetails/index.html in jellyfin-web)
    // rather than the "..." overflow menu, since that container class has
    // been stable across jellyfin-web releases for a long time - the
    // overflow menu's internal item list is more likely to change shape.

    function getItemIdFromHash() {
        var match = window.location.hash.match(/[?&]id=([a-f0-9-]+)/i);
        return match ? match[1] : null;
    }

    function isDetailsPage() {
        return window.location.hash.indexOf('#/details') === 0;
    }

    // Material Symbols Outlined "head_mounted_device" (FILL 0, wght 400, GRAD
    // 0, opsz 24), inlined as SVG rather than relying on jellyfin-web's
    // bundled icon font: that font is the older, frozen "Material Icons" set
    // (material-design-icons-iconfont) and doesn't include this glyph.
    var HEAD_MOUNTED_DEVICE_SVG =
        '<svg xmlns="http://www.w3.org/2000/svg" height="24" viewBox="0 -960 960 960" width="24" fill="currentColor" aria-hidden="true">' +
        '<path d="M300-240q-66 0-113-47t-47-113v-163q0-51 32-89.5t82-47.5q57-11 113-15.5t113-4.5q57 0 113.5 4.5T706-700q50 10 82 48t32 89v163q0 66-47 113t-113 47h-40q-13 0-26-1.5t-25-6.5l-64-22q-12-5-25-5t-25 5l-64 22q-12 5-25 6.5t-26 1.5h-40Zm0-80h40q7 0 13.5-1t12.5-3q29-9 56.5-19t57.5-10q30 0 58 9.5t56 19.5q6 2 12.5 3t13.5 1h40q33 0 56.5-23.5T740-400v-163q0-22-14-38t-35-21q-52-11-104.5-14.5T480-640q-54 0-106 4t-105 14q-21 4-35 20.5T220-563v163q0 33 23.5 56.5T300-320ZM40-400v-160h60v160H40Zm820 0v-160h60v160h-60Zm-380-80Z"/>' +
        '</svg>';

    function buildButton(itemId) {
        var btn = document.createElement('button');
        btn.type = 'button';
        btn.className = 'button-flat btnVrcShare detailButton emby-button';
        btn.dataset.itemId = itemId;
        btn.title = 'Create a time-limited VRChat share link';
        btn.innerHTML =
            '<div class="detailButton-content">' + HEAD_MOUNTED_DEVICE_SVG + '</div>';
        btn.addEventListener('click', function () {
            createShareLink(itemId, btn);
        });
        return btn;
    }

    function createShareLink(itemId, btn) {
        btn.disabled = true;
        btn.title = 'Creating share link…';

        var url = window.ApiClient.getUrl('VrcShare/CreateLink', { itemId: itemId });

        window.ApiClient.ajax({
            type: 'POST',
            url: url,
            dataType: 'json'
        }).then(function (result) {
            var minutes = Math.round((result.expires_at - Date.now() / 1000) / 60);
            return copyToClipboard(result.url).then(function () {
                notify('Share link copied! Valid for ~' + minutes + ' minutes.');
            });
        }).catch(function (err) {
            var message = (err && err.message) || 'Failed to create share link';
            notify(message, true);
        }).then(function () {
            btn.disabled = false;
            btn.title = 'Create a time-limited VRChat share link';
        });
    }

    function copyToClipboard(text) {
        if (navigator.clipboard && navigator.clipboard.writeText) {
            return navigator.clipboard.writeText(text);
        }
        // Fallback for contexts without the async clipboard API (e.g. non-HTTPS).
        var textarea = document.createElement('textarea');
        textarea.value = text;
        textarea.style.position = 'fixed';
        textarea.style.opacity = '0';
        document.body.appendChild(textarea);
        textarea.focus();
        textarea.select();
        try {
            document.execCommand('copy');
        } finally {
            document.body.removeChild(textarea);
        }
        return Promise.resolve();
    }

    function notify(message, isError) {
        if (window.Dashboard && typeof window.Dashboard.alert === 'function') {
            window.Dashboard.alert(message);
        } else {
            // eslint-disable-next-line no-alert
            window.alert(message);
        }
        if (isError) {
            console.error('[VrcShare]', message);
        }
    }

    // Cached admin-status lookup, shared across every addButtonIfNeeded()
    // invocation. The MutationObserver below can fire many times during a
    // single in-place episode transition, and jellyfin-web commonly aborts
    // in-flight ajax requests when a view transition happens - if each
    // invocation issued its own getCurrentUser() call, one landing inside
    // an abort window would reject, get swallowed by the catch() below, and
    // never retry once the DOM settled, leaving the button permanently
    // missing. Resolving this once and caching it means later invocations
    // in the same burst (or later bursts) just read the cached result
    // synchronously instead of racing another ajax call.
    var isAdminPromise = null;

    function getIsAdmin() {
        if (isAdminPromise) {
            return isAdminPromise;
        }
        if (!window.ApiClient || typeof window.ApiClient.getCurrentUser !== 'function') {
            return Promise.resolve(false);
        }
        isAdminPromise = window.ApiClient.getCurrentUser().then(function (user) {
            return !!(user && user.Policy && user.Policy.IsAdministrator);
        }).catch(function () {
            isAdminPromise = null;
            return false;
        });
        return isAdminPromise;
    }

    function addButtonIfNeeded() {
        if (!isDetailsPage()) {
            return;
        }

        var itemId = getItemIdFromHash();
        if (!itemId) {
            return;
        }

        var container = document.querySelector('.mainDetailButtons');
        if (!container) {
            return;
        }

        // The button row can survive an in-place navigation to a sibling
        // item (e.g. episode to episode) without being torn down, so a
        // present button isn't necessarily for the item now shown - only
        // skip if it's already tagged with the current item's id. Otherwise
        // drop the stale one and fall through to add a fresh one below.
        var existing = container.querySelector('.btnVrcShare');
        if (existing) {
            if (existing.dataset.itemId === itemId) {
                return;
            }
            existing.parentNode.removeChild(existing);
        }

        getIsAdmin().then(function (isAdmin) {
            if (!isAdmin) {
                return;
            }
            // The details view can be torn down and rebuilt - or navigated
            // away from entirely - while this lookup was in flight. Bail out
            // rather than act on a container/item that's no longer current;
            // whatever invocation is now responsible for the visible item
            // will run its own check.
            if (!isDetailsPage() || getItemIdFromHash() !== itemId) {
                return;
            }
            var freshContainer = document.querySelector('.mainDetailButtons');
            if (!freshContainer) {
                return;
            }
            var current = freshContainer.querySelector('.btnVrcShare');
            if (current) {
                if (current.dataset.itemId === itemId) {
                    return;
                }
                current.parentNode.removeChild(current);
            }
            var button = buildButton(itemId);
            var moreCommandsBtn = freshContainer.querySelector('.btnMoreCommands');
            if (moreCommandsBtn) {
                freshContainer.insertBefore(button, moreCommandsBtn);
            } else {
                freshContainer.appendChild(button);
            }
        }).catch(function () {
            // Not logged in yet, or request failed - just don't show the button.
        });
    }

    // jellyfin-web fires 'viewshow' on navigation between SPA views, but
    // navigating between two items that both use the details view (e.g.
    // episode to episode) reuses the same view instance and updates it in
    // place without firing 'viewshow'. A MutationObserver on the button row
    // reacts to that in-place re-render directly, instead of guessing when
    // it's finished with a fixed delay. It's the only mechanism that catches
    // this case, so it re-checks on every batch that actually changed the
    // DOM rather than debouncing to a single guess: the button row can take
    // more than one render pass to appear, and a guess that fires before the
    // last pass would otherwise never get a second chance.
    function onBodyMutation(mutations) {
        for (var i = 0; i < mutations.length; i++) {
            if (mutations[i].addedNodes.length > 0 || mutations[i].removedNodes.length > 0) {
                addButtonIfNeeded();
                return;
            }
        }
    }

    document.addEventListener('viewshow', addButtonIfNeeded);
    window.addEventListener('hashchange', addButtonIfNeeded);

    new MutationObserver(onBodyMutation).observe(document.body, {
        childList: true,
        subtree: true
    });
})();
