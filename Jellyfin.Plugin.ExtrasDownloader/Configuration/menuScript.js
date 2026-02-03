// Extras Downloader - Context Menu Integration
// Adds "Download Extras" to the More menu on Movie/Series detail pages

(function() {
    'use strict';

    const MENU_ITEM_ID = 'extrasDownloader';
    const SUPPORTED_TYPES = ['Movie', 'Series'];

    // Call the download API
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

    // Get current item info from the page
    function getCurrentItemInfo() {
        // Try URL params first
        const urlParams = new URLSearchParams(window.location.search);
        const itemId = urlParams.get('id');

        if (itemId) {
            return { id: itemId };
        }
        return null;
    }

    // Check if we're on a supported item type
    async function isSupportedItem(itemId) {
        try {
            const item = await ApiClient.getItem(ApiClient.getCurrentUserId(), itemId);
            return SUPPORTED_TYPES.includes(item.Type);
        } catch (e) {
            return false;
        }
    }

    // Create our menu item element
    function createMenuItem(itemId, itemName) {
        const menuItem = document.createElement('button');
        menuItem.setAttribute('is', 'paper-icon-button-light');
        menuItem.className = 'listItem listItem-button actionSheetMenuItem';
        menuItem.setAttribute('data-id', MENU_ITEM_ID);

        menuItem.innerHTML = `
            <span class="actionsheetMenuItemIcon listItemIcon listItemIcon-transparent material-icons download"></span>
            <div class="listItemBody actionsheetListItemBody">
                <div class="listItemBodyText actionSheetItemText">Download Extras</div>
            </div>
        `;

        menuItem.addEventListener('click', function(e) {
            e.preventDefault();
            e.stopPropagation();

            // Close the menu
            const dialog = document.querySelector('.actionSheet');
            if (dialog) {
                const closeBtn = dialog.querySelector('.btnCloseActionSheet');
                if (closeBtn) closeBtn.click();
            }

            // Trigger download
            downloadExtras(itemId, itemName || 'item');
        });

        return menuItem;
    }

    // Watch for the action sheet menu to appear and inject our item
    function watchForMenu() {
        const observer = new MutationObserver(function(mutations) {
            mutations.forEach(function(mutation) {
                mutation.addedNodes.forEach(function(node) {
                    if (node.nodeType === 1) {
                        // Check if an action sheet was added
                        const actionSheet = node.classList?.contains('actionSheet')
                            ? node
                            : node.querySelector?.('.actionSheet');

                        if (actionSheet) {
                            handleActionSheet(actionSheet);
                        }
                    }
                });
            });
        });

        observer.observe(document.body, {
            childList: true,
            subtree: true
        });
    }

    // Handle when action sheet appears
    async function handleActionSheet(actionSheet) {
        // Check if we already added our item
        if (actionSheet.querySelector(`[data-id="${MENU_ITEM_ID}"]`)) {
            return;
        }

        // Check if this looks like an item details menu (has Refresh metadata, Edit metadata, etc.)
        const menuItems = actionSheet.querySelectorAll('.actionSheetMenuItem');
        let isItemMenu = false;
        let insertAfter = null;

        menuItems.forEach(function(item) {
            const text = item.textContent?.toLowerCase() || '';
            if (text.includes('refresh metadata') || text.includes('edit metadata')) {
                isItemMenu = true;
                insertAfter = item;
            }
        });

        if (!isItemMenu) {
            return;
        }

        // Get current item
        const itemInfo = getCurrentItemInfo();
        if (!itemInfo) {
            return;
        }

        // Check if it's a supported type
        const supported = await isSupportedItem(itemInfo.id);
        if (!supported) {
            return;
        }

        // Get item name for the notification
        let itemName = 'item';
        try {
            const item = await ApiClient.getItem(ApiClient.getCurrentUserId(), itemInfo.id);
            itemName = item.Name;
        } catch (e) {
            // Use default
        }

        // Create and insert our menu item
        const menuItem = createMenuItem(itemInfo.id, itemName);

        if (insertAfter && insertAfter.parentNode) {
            insertAfter.parentNode.insertBefore(menuItem, insertAfter.nextSibling);
        } else {
            // Fallback: add to the end of the menu
            const menuList = actionSheet.querySelector('.actionSheetScroller');
            if (menuList) {
                menuList.appendChild(menuItem);
            }
        }
    }

    // Initialize
    function init() {
        if (window.ExtrasDownloaderInitialized) {
            return;
        }
        window.ExtrasDownloaderInitialized = true;

        watchForMenu();
        console.log('Extras Downloader menu integration loaded');
    }

    // Wait for page to be ready
    if (document.readyState === 'loading') {
        document.addEventListener('DOMContentLoaded', init);
    } else {
        init();
    }

    // Export for debugging
    window.ExtrasDownloader = {
        downloadExtras: downloadExtras
    };
})();
