#!/usr/bin/with-contenv bash
#####################################################################
# Radarr Extras Downloader for arr-scripts-v2
# 
# Adapted from RandomNinjaAtk/arr-scripts for standalone container use
# Downloads trailers, featurettes, clips, and other extras for movies
# using TMDB API and yt-dlp
#####################################################################

scriptVersion="3.9"
scriptName="Radarr-Extras"
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
    
    # Verify required Radarr settings
    if [ -z "$radarrUrl" ] || [ -z "$radarrApiKey" ]; then
        log "ERROR :: radarrUrl or radarrApiKey not set in settings.conf"
        log "ERROR :: Please set radarrUrl and radarrApiKey in /config/settings.conf"
        exit 1
    fi
    
    # Set the arr variables for compatibility
    arrUrl="$radarrUrl"
    arrApiKey="$radarrApiKey"
    
    # Set defaults if not defined
    extrasLanguages="${extrasLanguages:-en-US}"
    extrasType="${extrasType:-all}"
    extrasOfficialOnly="${extrasOfficialOnly:-false}"
    extrasSingle="${extrasSingle:-false}"
    extrasKodiCompatibility="${extrasKodiCompatibility:-false}"
    videoFormat="${videoFormat:-bestvideo*+bestaudio/best}"
    enableExtras="${enableExtras:-false}"
    onlyMissingTrailers="${onlyMissingTrailers:-false}"
    onlyUnreleased="${onlyUnreleased:-false}"
    potProviderUrl="${potProviderUrl:-}"
    
    # Sleep intervals (configurable via settings.conf)
    sleepBetweenVideos="${sleepBetweenVideos:-60}"
    sleepBetweenMovies="${sleepBetweenMovies:-300}"
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
    log "Verifying Radarr API access at $arrUrl..."
    local attempts=0
    local maxAttempts=5
    
    while [ $attempts -lt $maxAttempts ]; do
        arrApiTest=$(curl -s "$arrUrl/api/v3/system/status?apikey=$arrApiKey" | jq -r '.instanceName // empty' 2>/dev/null)
        
        if [ -n "$arrApiTest" ]; then
            log "Radarr API access verified: $arrApiTest"
            return 0
        fi
        
        attempts=$((attempts + 1))
        log "Radarr API not ready, attempt $attempts/$maxAttempts..."
        sleep 5
    done
    
    log "ERROR :: Could not connect to Radarr API after $maxAttempts attempts"
    return 1
}

#####################################################################
# INITIALIZATION
#####################################################################

# Setup logging first
logfileSetup

log "Starting Radarr Extras Script"

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
    log "ERROR :: Cannot connect to Radarr, exiting..."
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

#####################################################################
# MAIN PROCESSING FUNCTION
#####################################################################

processMovie() {
    local arrItemId="$1"
    local downloadAttempted=false
    
    # Get movie data from Radarr
    arrItemData=$(curl -s "$arrUrl/api/v3/movie/$arrItemId?apikey=$arrApiKey")
    
    if [ -z "$arrItemData" ] || [ "$arrItemData" == "null" ]; then
        log "ERROR :: Could not get movie data for ID: $arrItemId"
        return 1
    fi
    
    itemTitle=$(echo "$arrItemData" | jq -r '.title // empty')
    itemHasFile=$(echo "$arrItemData" | jq -r '.hasFile // false')
    itemPath=$(echo "$arrItemData" | jq -r '.path // empty')
    tmdbId=$(echo "$arrItemData" | jq -r '.tmdbId // empty')
    
    # Get release dates for unreleased check
    digitalRelease=$(echo "$arrItemData" | jq -r '.digitalRelease // empty')
    physicalRelease=$(echo "$arrItemData" | jq -r '.physicalRelease // empty')
    inCinemas=$(echo "$arrItemData" | jq -r '.inCinemas // empty')
    
    # Get movie file info
    itemFileData=$(curl -s "$arrUrl/api/v3/moviefile?movieId=$arrItemId&apikey=$arrApiKey")
    itemFileName=$(echo "$itemFileData" | jq -r '.[0].relativePath // empty')
    itemFileNameNoExt="${itemFileName%.*}"
    itemFolder=$(basename "$itemPath")
    
    if [ -z "$itemTitle" ]; then
        log "ERROR :: Could not get movie title for ID: $arrItemId"
        return 1
    fi
    
    # Check if only processing unreleased movies (not yet available for home viewing)
    if [ "$onlyUnreleased" == "true" ]; then
        today=$(date +%Y-%m-%d)
        sixtyDaysAgo=$(date -d "60 days ago" +%Y-%m-%d 2>/dev/null || date -v-60d +%Y-%m-%d 2>/dev/null)
        oneYearAgo=$(date -d "1 year ago" +%Y-%m-%d 2>/dev/null || date -v-1y +%Y-%m-%d 2>/dev/null)
        isReleased=false
        shouldProcess=false
        
        # First check: skip if ANY date is older than 1 year
        for releaseDate in "$digitalRelease" "$physicalRelease" "$inCinemas"; do
            if [ -n "$releaseDate" ] && [ "$releaseDate" != "null" ]; then
                releaseDateOnly="${releaseDate:0:10}"
                if [[ "$releaseDateOnly" < "$oneYearAgo" ]]; then
                    log "$itemTitle :: Has date older than 1 year ($releaseDateOnly), skipping (onlyUnreleased=true)"
                    return 2
                fi
            fi
        done
        
        # Check if digital or physical release date has passed
        for releaseDate in "$digitalRelease" "$physicalRelease"; do
            if [ -n "$releaseDate" ] && [ "$releaseDate" != "null" ]; then
                releaseDateOnly="${releaseDate:0:10}"
                if [[ "$releaseDateOnly" < "$today" ]] || [[ "$releaseDateOnly" == "$today" ]]; then
                    isReleased=true
                    break
                fi
            fi
        done
        
        # If not digitally/physically released, check if in theaters within last 60 days
        if [ "$isReleased" == "false" ]; then
            if [ -n "$inCinemas" ] && [ "$inCinemas" != "null" ]; then
                inCinemasOnly="${inCinemas:0:10}"
                # Movie is in theaters if: inCinemas date is in the past but within 60 days
                if [[ "$inCinemasOnly" > "$sixtyDaysAgo" ]] || [[ "$inCinemasOnly" == "$sixtyDaysAgo" ]]; then
                    if [[ "$inCinemasOnly" < "$today" ]] || [[ "$inCinemasOnly" == "$today" ]]; then
                        shouldProcess=true
                        log "$itemTitle :: Currently in theaters (since $inCinemasOnly), processing..."
                    fi
                fi
                # Movie hasn't been released yet at all
                if [[ "$inCinemasOnly" > "$today" ]]; then
                    shouldProcess=true
                fi
            else
                # No cinema date, no digital/physical release - likely upcoming
                shouldProcess=true
            fi
        fi
        
        if [ "$isReleased" == "true" ]; then
            log "$itemTitle :: Already released digitally/physically, skipping (onlyUnreleased=true)"
            return 2
        fi
        
        if [ "$shouldProcess" == "false" ]; then
            log "$itemTitle :: Theater release older than 60 days, skipping (onlyUnreleased=true)"
            return 2
        fi
    fi
    
    if [ ! -d "$itemPath" ]; then
        log "$itemTitle :: ERROR: Item Path does not exist ($itemPath), Skipping..."
        return 1
    fi
    
    if [ -z "$tmdbId" ] || [ "$tmdbId" == "null" ] || [ "$tmdbId" == "0" ]; then
        log "$itemTitle :: ERROR: No TMDB ID found, Skipping..."
        return 1
    fi
    
    # Check if only processing movies missing trailers
    if [ "$onlyMissingTrailers" == "true" ]; then
        trailerFolder="$itemPath/Trailers"
        # Also check for Kodi-style trailer
        kodiTrailer=$(find "$itemPath" -maxdepth 1 -name "*-trailer.*" 2>/dev/null | head -1)
        
        if [ -d "$trailerFolder" ] && [ "$(ls -A "$trailerFolder" 2>/dev/null)" ]; then
            log "$itemTitle :: Trailer already exists, skipping (onlyMissingTrailers=true)"
            return 2
        elif [ -n "$kodiTrailer" ]; then
            log "$itemTitle :: Trailer already exists (Kodi format), skipping (onlyMissingTrailers=true)"
            return 2
        fi
    fi
    
    log "$itemTitle :: Processing (TMDB: $tmdbId)"
    
    # Process each language filter
    IFS=',' read -r -a filters <<< "$extrasLanguages"
    for filter in "${filters[@]}"; do
        filter=$(echo "$filter" | xargs)  # trim whitespace
        
        log "$itemTitle :: Searching for \"$filter\" extras..."
        
        # Get videos from TMDB
        tmdbVideosListData=$(curl -s "https://api.themoviedb.org/3/movie/$tmdbId/videos?api_key=$tmdbApiKey&language=$filter" | jq -r '.results[] | select(.site=="YouTube")' 2>/dev/null)
        
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
                "Bloopers") extraFolderName="Deleted Scenes" ;;
                "Short") extraFolderName="Shorts" ;;
                "Interview") extraFolderName="Interviews" ;;
                *) extraFolderName="Other" ;;
            esac
            
            # Determine final path and filename
            if [ "$extrasSingle" == "true" ]; then
                log "$itemTitle :: $i of $tmdbVideosListDataIdsCount :: $tmdbExtraType :: Single Trailer Mode..."
                if [ "$extrasKodiCompatibility" == "true" ]; then
                    finalPath="$itemPath"
                    finalFileName="$itemFileNameNoExt-trailer"
                else
                    finalPath="$itemPath/$extraFolderName"
                    finalFileName="$itemFolder"
                fi
            else
                finalPath="$itemPath/$extraFolderName"
                if [ "$extraFolderName" == "Other" ]; then
                    finalFileName="$tmdbExtraTitleClean ($tmdbExtraType)"
                else
                    finalFileName="$tmdbExtraTitleClean"
                fi
            fi
            
            # Check if already exists
            if [ -f "$finalPath/$finalFileName.mkv" ]; then
                log "$itemTitle :: $i of $tmdbVideosListDataIdsCount :: $tmdbExtraType :: $tmdbExtraTitle :: Already exists, skipping..."
                if [ "$extrasSingle" == "true" ]; then
                    break
                fi
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
            
            # Break if single mode
            if [ "$extrasSingle" == "true" ]; then
                log "$itemTitle :: Single trailer mode - finished"
                break
            fi
            
            # Sleep between videos to avoid rate limiting (only after actual download attempt)
            log "$itemTitle :: Sleeping ${sleepBetweenVideos}s before next video..."
            sleep $sleepBetweenVideos
        done
    done
    
    log "$itemTitle :: Extras processing complete"
    
    # Return whether a download was attempted (for rate limiting decisions)
    if [ "$downloadAttempted" == "true" ]; then
        return 0
    else
        return 2  # Special return code: no download attempted
    fi
}

#####################################################################
# MAIN LOOP - Process all movies
#####################################################################

processAllMovies() {
    log "=========================================="
    log "Getting movie list from Radarr..."
    log "=========================================="
    
    # Get movies based on mode
    if [ "$onlyUnreleased" == "true" ]; then
        # Get all movies (including those without files) for unreleased mode
        movieIds=$(curl -s "$arrUrl/api/v3/movie?apikey=$arrApiKey" | jq -r '.[].id')
        log "Mode: Only processing unreleased movies"
    else
        # Get only movies with files
        movieIds=$(curl -s "$arrUrl/api/v3/movie?apikey=$arrApiKey" | jq -r '.[] | select(.hasFile == true) | .id')
    fi
    
    if [ -z "$movieIds" ]; then
        log "No movies found in Radarr"
        return
    fi
    
    movieCount=$(echo "$movieIds" | wc -l)
    log "Found $movieCount movies to process"
    log "Rate limiting: ${sleepBetweenVideos}s between videos, ${sleepBetweenMovies}s between movies (only when downloads occur)"
    if [ "$onlyMissingTrailers" == "true" ]; then
        log "Mode: Only processing movies missing trailers"
    fi
    
    current=0
    for movieId in $movieIds; do
        current=$((current + 1))
        log "=========================================="
        log "Processing movie $current of $movieCount (ID: $movieId)"
        log "=========================================="
        
        processMovie "$movieId"
        processResult=$?
        
        # Only sleep if a download was attempted (return code 0)
        # Skip sleep if no download attempted (return code 2) or error (return code 1)
        if [ $processResult -eq 0 ]; then
            log "Sleeping ${sleepBetweenMovies}s before next movie..."
            sleep $sleepBetweenMovies
        fi
    done
}

#####################################################################
# ENTRY POINT
#####################################################################

# Check if called with a specific movie ID (for custom script trigger)
# Ignore if $1 looks like a script name (from linuxserver service runner)
if [ -n "$1" ] && [[ "$1" =~ ^[0-9]+$ ]]; then
    log "Processing single movie ID: $1"
    processMovie "$1"
else
    # Continuous loop mode for service
    while true; do
        processAllMovies
        
        log "=========================================="
        log "Cycle complete. Sleeping for 24 hours..."
        log "=========================================="
        
        sleep 86400  # 24 hours
    done
fi

exit 0
