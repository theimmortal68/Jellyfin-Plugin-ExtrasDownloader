#!/usr/bin/with-contenv bash
#####################################################################
# Sonarr Extras Downloader for arr-scripts-v2
# 
# Adapted from RandomNinjaAtk/arr-scripts for standalone container use
# Downloads trailers, featurettes, clips, and other extras for TV series
# using TMDB API and yt-dlp
#####################################################################

scriptVersion="3.6"
scriptName="Sonarr-Extras"
tmdbApiKey="3b7751e3179f796565d88fdb2fcdf426"

#####################################################################
# FUNCTIONS (inline to avoid external dependency)
#####################################################################

log() {
    m_time=$(date "+%F %T")
    echo "$m_time :: $scriptName :: $scriptVersion :: $1"
    if [ -f "/config/logs/$logFileName" ]; then
        echo "$m_time :: $scriptName :: $scriptVersion :: $1" >> "/config/logs/$logFileName"
    fi
}

logfileSetup() {
    mkdir -p /config/logs
    logFileName="$scriptName-$(date +"%Y_%m_%d_%I_%M_%p").txt"
    
    # Auto-clean old log files
    find "/config/logs" -type f -iname "$scriptName-*.txt" -mtime +5 -delete 2>/dev/null
    
    if [ ! -f "/config/logs/$logFileName" ]; then
        touch "/config/logs/$logFileName"
        chmod 666 "/config/logs/$logFileName"
    fi
}

verifyConfig() {
    if [ ! -f "/config/settings.conf" ]; then
        echo "ERROR :: settings.conf not found in /config"
        echo "ERROR :: Please create settings.conf with required settings"
        exit 1
    fi
    
    source /config/settings.conf
    
    # Verify required Sonarr settings
    if [ -z "$sonarrUrl" ] || [ -z "$sonarrApiKey" ]; then
        log "ERROR :: sonarrUrl or sonarrApiKey not set in settings.conf"
        log "ERROR :: Please set sonarrUrl and sonarrApiKey in /config/settings.conf"
        exit 1
    fi
    
    # Set the arr variables for compatibility
    arrUrl="$sonarrUrl"
    arrApiKey="$sonarrApiKey"
    
    # Set defaults if not defined
    extrasLanguages="${extrasLanguages:-en-US}"
    extrasType="${extrasType:-all}"
    extrasOfficialOnly="${extrasOfficialOnly:-false}"
    videoFormat="${videoFormat:-bestvideo*+bestaudio/best}"
    enableExtras="${enableExtras:-false}"
    onlyMissingTrailers="${onlyMissingTrailers:-false}"
    onlyUnreleased="${onlyUnreleased:-false}"
    potProviderUrl="${potProviderUrl:-}"
    
    # Sleep intervals (configurable via settings.conf)
    sleepBetweenVideos="${sleepBetweenVideos:-60}"
    sleepBetweenSeries="${sleepBetweenSeries:-300}"
    ytdlpSleepInterval="${ytdlpSleepInterval:-30}"
    ytdlpMaxSleepInterval="${ytdlpMaxSleepInterval:-120}"
    
    # Build yt-dlp options
    ytdlpExtraOpts="--js-runtimes node --remote-components ejs:github --sleep-interval $ytdlpSleepInterval --max-sleep-interval $ytdlpMaxSleepInterval"
    if [ -n "$potProviderUrl" ]; then
        ytdlpExtraOpts="$ytdlpExtraOpts --extractor-args youtubepot-bgutilhttp:base_url=$potProviderUrl"
        log "POT Provider configured: $potProviderUrl"
    fi
}

verifyApiAccess() {
    log "Verifying Sonarr API access at $arrUrl..."
    local attempts=0
    local maxAttempts=5
    
    while [ $attempts -lt $maxAttempts ]; do
        arrApiTest=$(curl -s "$arrUrl/api/v3/system/status?apikey=$arrApiKey" | jq -r '.instanceName // empty' 2>/dev/null)
        
        if [ -n "$arrApiTest" ]; then
            log "Sonarr API access verified: $arrApiTest"
            return 0
        fi
        
        attempts=$((attempts + 1))
        log "Sonarr API not ready, attempt $attempts/$maxAttempts..."
        sleep 5
    done
    
    log "ERROR :: Could not connect to Sonarr API after $maxAttempts attempts"
    return 1
}

#####################################################################
# INITIALIZATION
#####################################################################

# Setup logging first
logfileSetup

log "Starting Sonarr Extras Script"

# Load and verify configuration
verifyConfig

# Check if extras are enabled
if [ "$enableExtras" != "true" ]; then
    log "Script is not enabled, enable by setting enableExtras to \"true\" in /config/settings.conf"
    log "Sleeping (infinity)"
    sleep infinity
fi

# Verify API access
if ! verifyApiAccess; then
    log "ERROR :: Cannot connect to Sonarr, exiting..."
    exit 1
fi

# Check for cookies file
if [ -f /config/cookies.txt ]; then
    cookiesFile="/config/cookies.txt"
    log "Cookies File Found!"
else
    log "Cookies File Not Found (optional)"
    cookiesFile=""
fi

# Create extras tracking directory
mkdir -p /config/logs/extras
chmod 777 /config/logs/extras

#####################################################################
# MAIN PROCESSING FUNCTION
#####################################################################

processSeries() {
    local arrItemId="$1"
    local downloadAttempted=false
    
    # Get series data from Sonarr
    arrItemData=$(curl -s "$arrUrl/api/v3/series/$arrItemId?apikey=$arrApiKey")
    
    if [ -z "$arrItemData" ] || [ "$arrItemData" == "null" ]; then
        log "ERROR :: Could not get series data for ID: $arrItemId"
        return 1
    fi
    
    itemTitle=$(echo "$arrItemData" | jq -r '.title // empty')
    itemPath=$(echo "$arrItemData" | jq -r '.path // empty')
    tvdbId=$(echo "$arrItemData" | jq -r '.tvdbId // empty')
    imdbId=$(echo "$arrItemData" | jq -r '.imdbId // empty')
    
    # Get first aired date for unreleased check
    firstAired=$(echo "$arrItemData" | jq -r '.firstAired // empty')
    seriesStatus=$(echo "$arrItemData" | jq -r '.status // empty')
    
    if [ -z "$itemTitle" ]; then
        log "ERROR :: Could not get series title for ID: $arrItemId"
        return 1
    fi
    
    # Check if only processing unreleased series
    if [ "$onlyUnreleased" == "true" ]; then
        today=$(date +%Y-%m-%d)
        isReleased=false
        
        # Check if first aired date has passed
        if [ -n "$firstAired" ] && [ "$firstAired" != "null" ]; then
            firstAiredOnly="${firstAired:0:10}"
            if [[ "$firstAiredOnly" < "$today" ]] || [[ "$firstAiredOnly" == "$today" ]]; then
                isReleased=true
            fi
        fi
        
        # Also consider status - if continuing/ended, it's released
        if [ "$seriesStatus" == "continuing" ] || [ "$seriesStatus" == "ended" ]; then
            isReleased=true
        fi
        
        if [ "$isReleased" == "true" ]; then
            log "$itemTitle :: Already released, skipping (onlyUnreleased=true)"
            return 2
        fi
    fi
    
    if [ ! -d "$itemPath" ]; then
        log "$itemTitle :: ERROR: Item Path does not exist ($itemPath), Skipping..."
        return 1
    fi
    
    # Check if only processing series missing trailers
    if [ "$onlyMissingTrailers" == "true" ]; then
        trailerFolder="$itemPath/Trailers"
        
        if [ -d "$trailerFolder" ] && [ "$(ls -A "$trailerFolder" 2>/dev/null)" ]; then
            log "$itemTitle :: Trailer already exists, skipping (onlyMissingTrailers=true)"
            return 2
        fi
    fi
    
    # Get TMDB ID from IMDB ID or TVDB ID
    tmdbId=""
    if [ -n "$imdbId" ] && [ "$imdbId" != "null" ]; then
        tmdbId=$(curl -s "https://api.themoviedb.org/3/find/$imdbId?api_key=$tmdbApiKey&external_source=imdb_id" | jq -r '.tv_results[0].id // empty')
    fi
    
    if [ -z "$tmdbId" ] && [ -n "$tvdbId" ] && [ "$tvdbId" != "null" ]; then
        tmdbId=$(curl -s "https://api.themoviedb.org/3/find/$tvdbId?api_key=$tmdbApiKey&external_source=tvdb_id" | jq -r '.tv_results[0].id // empty')
    fi
    
    if [ -z "$tmdbId" ] || [ "$tmdbId" == "null" ]; then
        log "$itemTitle :: ERROR: Could not find TMDB ID (IMDB: $imdbId, TVDB: $tvdbId), Skipping..."
        return 1
    fi
    
    # Check if already processed recently
    if [ -f "/config/logs/extras/$tmdbId" ]; then
        # Delete tracking file if older than 7 days
        find "/config/logs/extras" -type f -mtime +7 -name "$tmdbId" -delete 2>/dev/null
    fi
    
    if [ -f "/config/logs/extras/$tmdbId" ]; then
        log "$itemTitle :: Already processed within last 7 days, skipping..."
        return 2
    fi
    
    log "$itemTitle :: Processing (TMDB: $tmdbId)"
    
    # Process each language filter
    IFS=',' read -r -a filters <<< "$extrasLanguages"
    for filter in "${filters[@]}"; do
        filter=$(echo "$filter" | xargs)  # trim whitespace
        
        log "$itemTitle :: Searching for \"$filter\" extras..."
        
        # Get videos from TMDB (TV endpoint)
        tmdbVideosListData=$(curl -s "https://api.themoviedb.org/3/tv/$tmdbId/videos?api_key=$tmdbApiKey&language=$filter" | jq -r '.results[] | select(.site=="YouTube")' 2>/dev/null)
        
        if [ -z "$tmdbVideosListData" ]; then
            log "$itemTitle :: None found for $filter..."
            continue
        fi
        
        # Filter by type
        if [ "$extrasType" == "all" ]; then
            tmdbVideosListDataIds=$(echo "$tmdbVideosListData" | jq -r ".id" 2>/dev/null)
        else
            tmdbVideosListDataIds=$(echo "$tmdbVideosListData" | jq -r 'select(.type=="Trailer") | .id' 2>/dev/null)
        fi
        
        if [ -z "$tmdbVideosListDataIds" ]; then
            log "$itemTitle :: No matching extras found..."
            continue
        fi
        
        tmdbVideosListDataIdsCount=$(echo "$tmdbVideosListDataIds" | wc -l)
        log "$itemTitle :: $tmdbVideosListDataIdsCount Extras Found!"
        
        i=0
        for id in $tmdbVideosListDataIds; do
            i=$((i + 1))
            
            tmdbExtraData=$(echo "$tmdbVideosListData" | jq -r "select(.id==\"$id\")")
            tmdbExtraTitle=$(echo "$tmdbExtraData" | jq -r '.name // empty')
            tmdbExtraTitleClean=$(echo "$tmdbExtraTitle" | sed -e "s/[^[:alpha:][:digit:]$^&_+=()'%;{},.@#]/ /g" -e "s/  */ /g" | sed 's/^[.]*//' | sed 's/[.]*$//g' | sed 's/^ *//g' | sed 's/ *$//g')
            tmdbExtraKey=$(echo "$tmdbExtraData" | jq -r '.key // empty')
            tmdbExtraType=$(echo "$tmdbExtraData" | jq -r '.type // empty')
            tmdbExtraOfficial=$(echo "$tmdbExtraData" | jq -r '.official // false')
            
            # Skip unofficial if configured
            if [ "$tmdbExtraOfficial" != "true" ] && [ "$extrasOfficialOnly" == "true" ]; then
                log "$itemTitle :: $i of $tmdbVideosListDataIdsCount :: $tmdbExtraType :: Not official, skipping..."
                continue
            fi
            
            # Determine folder name based on type (Plex naming convention)
            case "$tmdbExtraType" in
                "Featurette") extraFolderName="Featurettes" ;;
                "Trailer") extraFolderName="Trailers" ;;
                "Teaser") extraFolderName="Trailers" ;;
                "Behind the Scenes") extraFolderName="Behind The Scenes" ;;
                "Clip") extraFolderName="Scenes" ;;
                "Opening Credits") extraFolderName="Featurettes" ;;
                "Interview") extraFolderName="Interviews" ;;
                *) extraFolderName="Other" ;;
            esac
            
            # Determine final path and filename
            finalPath="$itemPath/$extraFolderName"
            if [ "$extraFolderName" == "Other" ]; then
                finalFileName="$tmdbExtraTitleClean ($tmdbExtraType)"
            else
                finalFileName="$tmdbExtraTitleClean"
            fi
            
            # Check if already exists
            if [ -f "$finalPath/$finalFileName.mkv" ]; then
                log "$itemTitle :: $i of $tmdbVideosListDataIdsCount :: $tmdbExtraType :: $tmdbExtraTitle :: Already exists, skipping..."
                continue
            fi
            
            # Create temp and final directories
            tempFolder="/config/temp/extras/$arrItemId"
            rm -rf "$tempFolder" 2>/dev/null
            mkdir -p "$tempFolder"
            chmod 777 "$tempFolder" 2>/dev/null
            mkdir -p "$finalPath"
            chmod 777 "$finalPath" 2>/dev/null
            
            # Verify temp folder exists
            if [ ! -d "$tempFolder" ]; then
                log "$itemTitle :: ERROR: Could not create temp folder: $tempFolder"
                continue
            fi
            
            # Prepare video languages for subtitles
            videoLanguages=$(echo "$extrasLanguages" | sed "s/-[[:alpha:]][[:alpha:]]//g")
            
            log "$itemTitle :: $i of $tmdbVideosListDataIdsCount :: $tmdbExtraType :: $tmdbExtraTitle ($tmdbExtraKey) :: Downloading (yt-dlp :: $videoFormat)..."
            
            # Mark that we attempted a download
            downloadAttempted=true
            
            # Download with yt-dlp - use video ID as filename to avoid special character issues
            # First attempt: with cookies file if available, otherwise without
            if [ -n "$cookiesFile" ]; then
                yt-dlp -f "$videoFormat" --no-video-multistreams --cookies "$cookiesFile" \
                    -o "$tempFolder/%(id)s.%(ext)s" --write-sub --sub-lang $videoLanguages \
                    --embed-subs --merge-output-format mkv --no-mtime --geo-bypass \
                    $ytdlpExtraOpts "https://www.youtube.com/watch?v=$tmdbExtraKey" 2>&1
                downloadResult=$?
            else
                yt-dlp -f "$videoFormat" --no-video-multistreams \
                    -o "$tempFolder/%(id)s.%(ext)s" --write-sub --sub-lang $videoLanguages \
                    --embed-subs --merge-output-format mkv --no-mtime --geo-bypass \
                    $ytdlpExtraOpts "https://www.youtube.com/watch?v=$tmdbExtraKey" 2>&1
                downloadResult=$?
            fi
            
            # Check if download failed and Firefox profile exists (for age-restricted content)
            downloadedFile=$(find "$tempFolder" -name "*.mkv" -type f 2>/dev/null | head -1)
            
            if [ -z "$downloadedFile" ] && [ -d "/firefox-profile" ]; then
                log "$itemTitle :: $i of $tmdbVideosListDataIdsCount :: $tmdbExtraType :: $tmdbExtraTitle :: Retrying with Firefox cookies (age-restricted?)..."
                
                # Retry with Firefox cookies from profile
                yt-dlp -f "$videoFormat" --no-video-multistreams --cookies-from-browser firefox:/firefox-profile \
                    -o "$tempFolder/%(id)s.%(ext)s" --write-sub --sub-lang $videoLanguages \
                    --embed-subs --merge-output-format mkv --no-mtime --geo-bypass \
                    $ytdlpExtraOpts "https://www.youtube.com/watch?v=$tmdbExtraKey" 2>&1
                
                downloadedFile=$(find "$tempFolder" -name "*.mkv" -type f 2>/dev/null | head -1)
            fi
            
            if [ -n "$downloadedFile" ]; then
                log "$itemTitle :: $i of $tmdbVideosListDataIdsCount :: $tmdbExtraType :: $tmdbExtraTitle :: Download Complete"
                
                # Move to final destination with clean name
                mv "$downloadedFile" "$finalPath/$finalFileName.mkv"
                chmod 666 "$finalPath/$finalFileName.mkv" 2>/dev/null
                
                log "$itemTitle :: $i of $tmdbVideosListDataIdsCount :: $tmdbExtraType :: $tmdbExtraTitle :: Moved to $finalPath"
            else
                log "$itemTitle :: $i of $tmdbVideosListDataIdsCount :: $tmdbExtraType :: $tmdbExtraTitle :: ERROR :: Download Failed"
            fi
            
            # Cleanup temp
            rm -rf "$tempFolder" 2>/dev/null
            
            # Sleep between videos to avoid rate limiting
            log "$itemTitle :: Sleeping ${sleepBetweenVideos}s before next video..."
            sleep $sleepBetweenVideos
        done
    done
    
    # Mark as processed
    touch "/config/logs/extras/$tmdbId"
    chmod 666 "/config/logs/extras/$tmdbId"
    log "$itemTitle :: Marked as processed (tracking: /config/logs/extras/$tmdbId)"
    
    log "$itemTitle :: Extras processing complete"
    
    # Return whether a download was attempted (for rate limiting decisions)
    if [ "$downloadAttempted" == "true" ]; then
        return 0
    else
        return 2  # Special return code: no download attempted
    fi
}

#####################################################################
# MAIN LOOP - Process all series
#####################################################################

processAllSeries() {
    log "=========================================="
    log "Getting series list from Sonarr..."
    log "=========================================="
    
    # Get series based on mode
    if [ "$onlyUnreleased" == "true" ]; then
        # Get all series (including those without files) for unreleased mode
        seriesIds=$(curl -s "$arrUrl/api/v3/series?apikey=$arrApiKey" | jq -r '.[].id')
        log "Mode: Only processing unreleased series"
    else
        # Get only series with files
        seriesIds=$(curl -s "$arrUrl/api/v3/series?apikey=$arrApiKey" | jq -r '.[] | select(.statistics.episodeFileCount > 0) | .id')
    fi
    
    if [ -z "$seriesIds" ]; then
        log "No series found in Sonarr"
        return
    fi
    
    seriesCount=$(echo "$seriesIds" | wc -l)
    log "Found $seriesCount series to process"
    log "Rate limiting: ${sleepBetweenVideos}s between videos, ${sleepBetweenSeries}s between series (only when downloads occur)"
    if [ "$onlyMissingTrailers" == "true" ]; then
        log "Mode: Only processing series missing trailers"
    fi
    
    current=0
    for seriesId in $seriesIds; do
        current=$((current + 1))
        log "=========================================="
        log "Processing series $current of $seriesCount (ID: $seriesId)"
        log "=========================================="
        
        processSeries "$seriesId"
        processResult=$?
        
        # Only sleep if a download was attempted (return code 0)
        # Skip sleep if no download attempted (return code 2) or error (return code 1)
        if [ $processResult -eq 0 ]; then
            log "Sleeping ${sleepBetweenSeries}s before next series..."
            sleep $sleepBetweenSeries
        fi
    done
}

#####################################################################
# ENTRY POINT
#####################################################################

# Check if called with a specific series ID (for custom script trigger)
# Ignore if $1 looks like a script name (from linuxserver service runner)
if [ -n "$1" ] && [[ "$1" =~ ^[0-9]+$ ]]; then
    log "Processing single series ID: $1"
    processSeries "$1"
else
    # Continuous loop mode for service
    while true; do
        processAllSeries
        
        log "=========================================="
        log "Cycle complete. Sleeping for 24 hours..."
        log "=========================================="
        
        sleep 86400  # 24 hours
    done
fi

exit 0
