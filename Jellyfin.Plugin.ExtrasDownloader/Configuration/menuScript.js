// Extras Downloader - Context Menu Integration
// This script adds a "Download Extras" option to the context menu for Movies and Series

(function() {
    'use strict';

    const PLUGIN_ID = 'd2a0abca-4598-4d40-97bc-f74966738645';

    // Function to call the download API
    async function downloadExtras(itemId, itemName) {
        try {
            const response = await ApiClient.ajax({
                type: 'POST',
                url: ApiClient.getUrl('ExtrasDownloader/Download/' + itemId),
                dataType: 'json'
            });

            if (response.Success) {
                Dashboard.alert({
                    message: 'Queued "' + itemName + '" for extras download. Check Scheduled Tasks for progress.',
                    title: 'Extras Downloader'
                });
            } else {
                Dashboard.alert({
                    message: response.Message || 'Failed to queue download',
                    title: 'Extras Downloader Error'
                });
            }
        } catch (error) {
            console.error('ExtrasDownloader error:', error);
            Dashboard.alert({
                message: 'Failed to queue download: ' + (error.message || 'Unknown error'),
                title: 'Extras Downloader Error'
            });
        }
    }

    // Hook into Jellyfin's context menu system
    function addContextMenuItem() {
        // Check if we've already added our menu item
        if (window.ExtrasDownloaderMenuAdded) {
            return;
        }

        // Listen for the context menu to be shown
        document.addEventListener('contextmenu', function(e) {
            // This is a fallback - the main integration is through itemcontextmenu event
        });

        // The proper way to add context menu items in Jellyfin
        if (typeof window.addEventListener === 'function') {
            window.addEventListener('message', function(event) {
                if (event.data && event.data.type === 'showitemcontextmenu') {
                    // This would be the proper hook point
                }
            });
        }

        window.ExtrasDownloaderMenuAdded = true;
    }

    // Alternative: Add a button to the detail page
    function addDetailPageButton() {
        // Observer to watch for detail pages being loaded
        const observer = new MutationObserver(function(mutations) {
            mutations.forEach(function(mutation) {
                if (mutation.addedNodes.length) {
                    checkForDetailPage();
                }
            });
        });

        observer.observe(document.body, {
            childList: true,
            subtree: true
        });

        // Also check on page show
        document.addEventListener('viewshow', checkForDetailPage);
    }

    function checkForDetailPage() {
        // Look for the item detail page
        const detailPage = document.querySelector('.itemDetailPage');
        if (!detailPage) return;

        // Check if we already added our button
        if (detailPage.querySelector('.btnDownloadExtras')) return;

        // Get item info from the page
        const itemId = getItemIdFromPage();
        if (!itemId) return;

        // Find the button container (usually near Refresh Metadata, etc.)
        const moreCommandsButton = detailPage.querySelector('.btnMoreCommands');
        if (!moreCommandsButton) return;

        // We'll add our functionality through the more menu
        // For now, let's try to hook into the existing menu system
    }

    function getItemIdFromPage() {
        // Try to get item ID from URL
        const urlParams = new URLSearchParams(window.location.search);
        const itemId = urlParams.get('id');
        if (itemId) return itemId;

        // Try to get from the page data
        const detailPage = document.querySelector('.itemDetailPage');
        if (detailPage && detailPage.getAttribute('data-id')) {
            return detailPage.getAttribute('data-id');
        }

        return null;
    }

    // Initialize when DOM is ready
    if (document.readyState === 'loading') {
        document.addEventListener('DOMContentLoaded', function() {
            addContextMenuItem();
            addDetailPageButton();
        });
    } else {
        addContextMenuItem();
        addDetailPageButton();
    }

    // Export for manual use
    window.ExtrasDownloader = {
        downloadExtras: downloadExtras,
        PLUGIN_ID: PLUGIN_ID
    };

    console.log('Extras Downloader menu script loaded');
})();
