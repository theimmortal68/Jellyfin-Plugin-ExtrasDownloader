#!/bin/bash
#####################################################################
# Rename Extras Folders to Plex-Compatible Names
# 
# This script renames extras folders from lowercase to Plex's
# expected Title Case naming convention.
#
# Usage: ./rename-extras-folders.sh /path/to/movies [--dry-run]
#        ./rename-extras-folders.sh /path/to/tv [--dry-run]
#
# Use --dry-run first to see what would be renamed without making changes
#####################################################################

# Colors for output
RED='\033[0;31m'
GREEN='\033[0;32m'
YELLOW='\033[1;33m'
NC='\033[0m' # No Color

# Check arguments
if [ -z "$1" ]; then
    echo -e "${RED}Error: Please provide a path to your media folder${NC}"
    echo ""
    echo "Usage: $0 /path/to/media [--dry-run]"
    echo ""
    echo "Examples:"
    echo "  $0 /mnt/user/data/media/movies --dry-run    # Preview changes for movies"
    echo "  $0 /mnt/user/data/media/movies              # Apply changes for movies"
    echo "  $0 /mnt/user/data/media/tv --dry-run        # Preview changes for TV shows"
    echo "  $0 /mnt/user/data/media/tv                  # Apply changes for TV shows"
    exit 1
fi

MEDIA_PATH="$1"
DRY_RUN=false

if [ "$2" == "--dry-run" ]; then
    DRY_RUN=true
    echo -e "${YELLOW}=== DRY RUN MODE - No changes will be made ===${NC}"
    echo ""
fi

if [ ! -d "$MEDIA_PATH" ]; then
    echo -e "${RED}Error: Directory not found: $MEDIA_PATH${NC}"
    exit 1
fi

echo "Scanning: $MEDIA_PATH"
echo ""

# Counter for changes
renamed_count=0
skipped_count=0

# Define folder name mappings (old -> new)
declare -A FOLDER_MAP=(
    ["trailers"]="Trailers"
    ["trailer"]="Trailers"
    ["featurettes"]="Featurettes"
    ["featurette"]="Featurettes"
    ["behind the scenes"]="Behind The Scenes"
    ["behindthescenes"]="Behind The Scenes"
    ["clips"]="Scenes"
    ["clip"]="Scenes"
    ["scenes"]="Scenes"
    ["scene"]="Scenes"
    ["deleted scenes"]="Deleted Scenes"
    ["deletedscenes"]="Deleted Scenes"
    ["deleted"]="Deleted Scenes"
    ["bloopers"]="Deleted Scenes"
    ["blooper"]="Deleted Scenes"
    ["interviews"]="Interviews"
    ["interview"]="Interviews"
    ["shorts"]="Shorts"
    ["short"]="Shorts"
    ["extras"]="Featurettes"
    ["extra"]="Featurettes"
    ["other"]="Other"
)

# Function to rename a folder
rename_folder() {
    local old_path="$1"
    local new_name="$2"
    local parent_dir=$(dirname "$old_path")
    local new_path="$parent_dir/$new_name"
    
    # Skip if already correct
    if [ "$old_path" == "$new_path" ]; then
        return 0
    fi
    
    # Check if target already exists
    if [ -d "$new_path" ]; then
        echo -e "${YELLOW}  MERGE: $old_path -> $new_path (target exists, merging contents)${NC}"
        if [ "$DRY_RUN" == "false" ]; then
            # Move contents to existing folder
            mv "$old_path"/* "$new_path"/ 2>/dev/null
            # Remove empty source folder
            rmdir "$old_path" 2>/dev/null
        fi
    else
        echo -e "${GREEN}  RENAME: $old_path -> $new_path${NC}"
        if [ "$DRY_RUN" == "false" ]; then
            mv "$old_path" "$new_path"
        fi
    fi
    
    ((renamed_count++))
}

# Find all potential extras folders
echo "Looking for extras folders to rename..."
echo ""

# Search for each old folder name pattern
for old_name in "${!FOLDER_MAP[@]}"; do
    new_name="${FOLDER_MAP[$old_name]}"
    
    # Find folders matching this pattern (case-insensitive)
    while IFS= read -r -d '' folder; do
        # Get the actual folder name
        folder_basename=$(basename "$folder")
        folder_lower=$(echo "$folder_basename" | tr '[:upper:]' '[:lower:]')
        
        # Check if it matches our pattern (case-insensitive)
        if [ "$folder_lower" == "$old_name" ] && [ "$folder_basename" != "$new_name" ]; then
            rename_folder "$folder" "$new_name"
        fi
    done < <(find "$MEDIA_PATH" -type d -iname "$old_name" -print0 2>/dev/null)
done

echo ""
echo "=========================================="
if [ "$DRY_RUN" == "true" ]; then
    echo -e "${YELLOW}DRY RUN COMPLETE${NC}"
    echo "Would rename: $renamed_count folders"
    echo ""
    echo "Run without --dry-run to apply changes:"
    echo "  $0 $MEDIA_PATH"
else
    echo -e "${GREEN}COMPLETE${NC}"
    echo "Renamed: $renamed_count folders"
fi
echo "=========================================="
