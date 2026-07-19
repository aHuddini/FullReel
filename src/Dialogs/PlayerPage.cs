
namespace FullVid.Dialogs
{
    // Builds the hosted YouTube player page (HTML + CSS + in-page JS) and the embed-frame
    // script. Pure string generation extracted from VideoPlayerDialog — no WPF/instance state.
    // The dialog drives all transport from C# via ExecuteScriptAsync against this page.
    internal static class PlayerPage
    {
        // Per-style glass skin for the bars (in-page CSS, backdrop-filter over the live video).
        // Returns (topBackground, bottomBackground, blurPx, topBorderCss, bottomBorderCss).
        internal static void GetBarSkin(PlayerBarStyle style, out string topBg, out string botBg,
            out int blur, out string topBorder, out string botBorder)
        {
            switch (style)
            {
                case PlayerBarStyle.HeavyFrost:
                    topBg = "rgba(40,40,44,.55)"; botBg = "rgba(40,40,44,.5)"; blur = 28;
                    topBorder = "1px solid rgba(255,255,255,.35)"; botBorder = "1px solid rgba(255,255,255,.35)";
                    break;
                case PlayerBarStyle.TintedPurple:
                    topBg = "rgba(52,34,78,.62)"; botBg = "rgba(52,34,78,.5)"; blur = 18;
                    topBorder = "1px solid rgba(179,157,219,.4)"; botBorder = "1px solid rgba(179,157,219,.4)";
                    break;
                case PlayerBarStyle.MinimalGlass:
                    topBg = "rgba(20,20,20,.32)"; botBg = "rgba(20,20,20,.22)"; blur = 8;
                    topBorder = "1px solid rgba(255,255,255,.08)"; botBorder = "1px solid rgba(255,255,255,.1)";
                    break;
                case PlayerBarStyle.GradientFade:
                    // No hard edge: gradients fade into the video. Blur is light on top of that.
                    topBg = "linear-gradient(to bottom,rgba(0,0,0,.75),rgba(0,0,0,0))";
                    botBg = "linear-gradient(to top,rgba(0,0,0,.75),rgba(0,0,0,0))";
                    blur = 6; topBorder = "0"; botBorder = "0";
                    break;
                default: // FrostedBlur — the official look
                    topBg = "rgba(10,10,10,.72)"; botBg = "rgba(18,18,18,.35)"; blur = 16;
                    topBorder = "1px solid rgba(255,255,255,.15)"; botBorder = "1px solid rgba(255,255,255,.25)";
                    break;
            }
        }

        // Builds the hosted player page. In every glass style the controls-hint legend is an
        // in-page overlay with backdrop-filter — Chromium blurs the live video on the GPU, no WPF.
        // GradientFade adds extra bottom padding on the top bar so the gradient has room to fade.
        internal static string BuildPlayerHtml(string videoId, string title, PlayerBarStyle style, bool frostedBar, bool keepBarOverBlack)
        {
            var safeId = System.Text.RegularExpressions.Regex.Replace(videoId ?? string.Empty, "[^A-Za-z0-9_-]", "");

            GetBarSkin(style, out var topBg, out var botBg, out var blur, out var topBorder, out var botBorder);
            // GradientFade uses taller bars so the gradient has room to fade.
            var topPad = style == PlayerBarStyle.GradientFade ? "18px 18px 30px" : "12px 18px";
            var botPad = style == PlayerBarStyle.GradientFade ? "30px 8px 14px" : "13px 8px";
            // Pill accent: violet on the TintedPurple skin, neutral glass on the rest. Low bg
            // alpha + soft border so the bar's blur shows through and the pill reads subtly.
            var pillCss = style == PlayerBarStyle.TintedPurple
                ? "color:#E6DFF7;background:rgba(139,92,246,.14);border:1px solid rgba(179,157,219,.22)"
                : "color:#EDEDED;background:rgba(255,255,255,.06);border:1px solid rgba(255,255,255,.14)";

            // Single <style> block with classes, not inline styles — both bars share .fvbar so
            // the glass (tint + blur + border) is identical on each; only the edge border and
            // background differ per bar/skin. Both bars carry glass DIRECTLY on the element (no
            // ::before) — a ::before glass layer once caused a bottom-edge seam because the
            // pseudo-element's edge didn't align with the parent's in the live compositor.
            var css =
                "html,body{margin:0;height:100%;background:#000;overflow:hidden}" +
                // #mouseshield: a transparent layer OVER the video iframe that swallows mouse
                // events, so the cursor sitting over the player never reaches YouTube's embed and
                // its hover/tooltip UI never appears. Sits below the control bars (lower z) so the
                // quality pill still gets clicks; the player is controller/keyboard driven so the
                // video needs no mouse interaction.
                "#mouseshield{position:fixed;inset:0;z-index:2147483645}" +
                // #p (the YT iframe) is a centered 16:9 COVER block. Cover sizing keeps real video
                // under every edge; the overflow sliver is cropped.
                // keepBarOverBlack ON: +4px on BOTH axes so the video box never EXACTLY matches the
                // window client rect (WebView2 #5574: an exact-match box is promoted to a
                // full-surface overlay that stale-skips the page — glass bars vanish over black
                // frames; the 1280x720 windowed player is the exact-match case, fullscreen
                // overshoots via cover-crop and is fine). 4px is the reliable threshold (3px still
                // let the bar vanish). Symmetric and centered, so the extra crop is even and
                // imperceptible. OFF: exact original framing (bar may vanish over black).
                "#p{position:fixed;left:50%;top:50%;transform:translate(-50%,-50%);" +
                (keepBarOverBlack
                    ? "width:calc(max(100vw,177.7778vh) + 4px);height:calc(max(100vh,56.25vw) + 4px)}"
                    : "width:max(100vw,177.7778vh);height:max(100vh,56.25vw)}") +
                // Shared bar glass. pointer-events:none = display-only; all input stays in C#.
                ".fvbar{position:fixed;left:0;right:0;z-index:2147483647;pointer-events:none;" +
                "box-sizing:border-box;color:#F5F5F5;" +
                "backdrop-filter:blur(" + blur + "px) saturate(1.2);" +
                "-webkit-backdrop-filter:blur(" + blur + "px) saturate(1.2);" +
                "transition:transform .35s ease,opacity .35s ease}" +
                ".fvbar-top{top:0;padding:" + topPad + ";font:600 16px 'Segoe UI',sans-serif;color:#FFF;" +
                "white-space:nowrap;overflow:hidden;text-overflow:ellipsis;" +
                "background:" + topBg + ";border-bottom:" + topBorder + "}" +
                ".fvbar-bottom{bottom:0;padding:" + botPad + ";font:14px 'Segoe UI',sans-serif;" +
                "background:" + botBg + ";border-top:" + botBorder + ";" +
                // Fixed content-row height so nothing that appears after load (the quality pill,
                // the ticking time) can grow the row and shift the bar up a pixel. 21px = the
                // tallest cell (the pill: 11px text + 2px*2 pad + 1px*2 border).
                "display:grid;grid-template-columns:1fr auto 1fr;align-items:center;column-gap:12px;" +
                "grid-auto-rows:21px}" +
                ".fvleft{text-align:left;padding-left:6px}" +
                // Pill reserves its height from the start (min-width keeps it from popping wider
                // either). When empty it's visibility:hidden — still occupies layout — so the
                // resolution label appearing ~1-2s in doesn't reflow/grow the bar row.
                ".fvpill{display:inline-block;pointer-events:auto;cursor:pointer;" +
                "font:600 11px 'Segoe UI',sans-serif;border-radius:999px;padding:2px 10px;" +
                "line-height:15px;min-width:34px;text-align:center;box-sizing:border-box;" +
                "letter-spacing:.3px;" + pillCss + "}" +
                "#qual:empty{visibility:hidden}" +
                ".fvlegend{text-align:center}" +
                ".fvsep{color:rgba(255,255,255,.4)}" +
                ".fvkey{color:#B39DDB}.fvkey-close{color:#EF9A9A}" +
                ".fvtime{text-align:right;font:600 12px 'Segoe UI',sans-serif;color:#BBB;padding-right:6px}";

            // Brand mark: the FullReel reel + play-triangle hub, inline SVG so it stays crisp at
            // bar size. Violet→pink gradient matches the branding.
            const string brandMark =
                "<svg width='26' height='26' viewBox='0 0 120 120' style='vertical-align:-7px;margin-right:10px'>" +
                "<defs><linearGradient id='fvlg' x1='0' y1='0' x2='1' y2='1'>" +
                "<stop offset='0' stop-color='#8B5CF6'/><stop offset='1' stop-color='#EC4899'/></linearGradient></defs>" +
                "<circle cx='60' cy='60' r='46' fill='none' stroke='url(#fvlg)' stroke-width='11'/>" +
                "<circle cx='60' cy='29' r='9' fill='url(#fvlg)' fill-opacity='.6'/>" +
                "<circle cx='89' cy='50' r='9' fill='url(#fvlg)' fill-opacity='.6'/>" +
                "<circle cx='78' cy='85' r='9' fill='url(#fvlg)' fill-opacity='.6'/>" +
                "<circle cx='42' cy='85' r='9' fill='url(#fvlg)' fill-opacity='.6'/>" +
                "<circle cx='31' cy='50' r='9' fill='url(#fvlg)' fill-opacity='.6'/>" +
                "<path d='M 50 45 L 76 60 L 50 75 Z' fill='#F5F5F5'/></svg>";

            var topBar = !frostedBar ? "" :
                "<div id=\"tbar\" class=\"fvbar fvbar-top\">" + brandMark + HtmlEscape(title) + "</div>";

            // Legend: controls centered, "current / total" at the far right; grid keeps the
            // legend truly centered regardless of the time width.
            const string sep = "<span class=\"fvsep\">&nbsp;•&nbsp;</span>";
            // Terse labels to fit more controls in the bar's real estate. Quality (LB/Q) added.
            const string legend =
                "<b class=\"fvkey\">A / Space</b> Play" + sep +
                "<b class=\"fvkey\">◄ ►</b> Seek" + sep +
                "<b class=\"fvkey\">▲ ▼</b> Vol" + sep +
                "<b class=\"fvkey\">LB / Q</b> Quality" + sep +
                "<b class=\"fvkey\">Y / D</b> Save" + sep +
                "<b class=\"fvkey\">Select / F</b> Fullscreen" + sep +
                "<b class=\"fvkey\">RB / P</b> Hide UI" + sep +
                "<b class=\"fvkey-close\">B / Esc</b> Close";

            var bottomBar = !frostedBar ? "" :
                "<div id=\"bbar\" class=\"fvbar fvbar-bottom\">" +
                // Pill: live playing-resolution label; click cycles quality auto→720→…→2160.
                "<span class=\"fvleft\"><span id=\"qual\" class=\"fvpill\" title=\"Click: change quality\"></span></span>" +
                "<span class=\"fvlegend\">" + legend + "</span>" +
                "<span class=\"fvtime\"><span id=\"cur\">0:00</span> / <span id=\"tot\">0:00</span></span>" +
                "</div>";

            return
                "<!DOCTYPE html><html><head><meta charset=\"utf-8\">" +
                "<style>" + css + "</style>" +
                "</head><body><div id=\"p\"></div><div id=\"mouseshield\"></div>" +
                topBar + bottomBar +
                "<script>" +
                "var player;" +
                "function onYouTubeIframeAPIReady(){" +
                "  player=new YT.Player('p',{videoId:'" + safeId + "'," +
                // vq=hd1080 requests a high-res stream (60fps lives at 1080p+); YouTube treats it
                // as a hint. onReady also suggests hd1080. Final quality is YouTube's call — the
                // embed biases toward 'auto', so 60fps only plays if the video has it + net allows.
                // cc_load_policy:0 = don't force captions on; iv_load_policy:3 = hide annotations.
                "    playerVars:{autoplay:1,controls:0,modestbranding:1,rel:0,playsinline:1,vq:'hd1080'," +
                "cc_load_policy:0,iv_load_policy:3}," +
                "    events:{" +
                // Tell C# the YT player object is live. Until this fires, transport scripts
                // (if(window.player){...}) silently no-op, so early controller/key presses vanish —
                // C# holds the first press and replays it on 'ready'.
                "      onReady:function(){try{player.setPlaybackQuality('hd1080');}catch(e){}" +
                "        fvKillCaptions();" +
                "        try{chrome.webview.postMessage('ready');}catch(e){}}," +
                // On PLAYING (1) reveal the top bar AND re-kill captions — YouTube re-inits the CC
                // module when playback starts, so an onReady-only unload isn't enough.
                "      onStateChange:function(e){if(e.data===1){fvShowTop();fvKillCaptions();}}}});" +
                "}" +
                // Kill captions thoroughly: unload the CC modules AND clear the active track via
                // setOption. Runs on ready, on play, and a few delayed retries — YouTube can
                // re-enable captions asynchronously after the first frame.
                "window.fvKillCaptions=function(){try{if(!window.player)return;" +
                "try{player.unloadModule('captions');}catch(e){}" +
                "try{player.unloadModule('cc');}catch(e){}" +
                "try{player.setOption('captions','track',{});}catch(e){}" +
                "try{player.setOption('cc','track',{});}catch(e){}}catch(e){}};" +
                "setTimeout(fvKillCaptions,1200);setTimeout(fvKillCaptions,3000);" +
                "var s=document.createElement('script');s.src='https://www.youtube.com/iframe_api';" +
                "document.head.appendChild(s);" +
                // Bar visibility. The TOP bar always auto-hides after 4s (its established behavior).
                // The BOTTOM bar only auto-hides when fvBottomAuto is on — C# sets that ONLY while
                // the player is expanded to fullscreen, so the windowed player keeps its bar shown.
                // fvShow() reveals both + rearms the timers; C# pokes it on every input.
                "var _tt,_tb,fvBottomAuto=false,_botShown=true;" +
                "function _set(id,show,dir){var e=document.getElementById(id);if(!e)return;" +
                "if(id==='bbar')_botShown=!!show;" +
                "e.style.opacity=show?'1':'0';e.style.transform=show?'translateY(0)':('translateY('+dir+'100%)');}" +
                // Bottom bar: shown by default and its ONLY hide path is its own 4s timer, armed
                // solely when fvBottomAuto is on (fullscreen auto-hide). fvShow (fired on play/
                // input) never touches the bottom timer, so it can't drift to hiding on its own.
                "window.fvSetBottomAuto=function(on){fvBottomAuto=!!on;" +
                "if(_tb)clearTimeout(_tb);_set('bbar',1,'');" +
                "if(fvBottomAuto)_tb=setTimeout(function(){_set('bbar',0,'');},4000);};" +
                // fvShow reveals both bars and (re)arms ONLY the top bar's auto-hide. It re-shows
                // the bottom bar and, if auto-hide is on, restarts its timer — but it never hides.
                "window.fvShow=function(){_set('tbar',1,'-');_set('bbar',1,'');" +
                "if(_tt)clearTimeout(_tt);_tt=setTimeout(function(){_set('tbar',0,'-');},4000);" +
                "if(fvBottomAuto){if(_tb)clearTimeout(_tb);_tb=setTimeout(function(){_set('bbar',0,'');},4000);}};" +
                "window.fvShowTop=window.fvShow;" +
                // Quality label: the embed-side script (BuildEmbedScript) posts {fvq:'1080p'} from
                // inside the iframe on every adaptive resolution switch; only YouTube frames are
                // accepted and only the expected string shape is rendered.
                "window.addEventListener('message',function(e){try{" +
                "if(!/(^|\\.)youtube(-nocookie)?\\.com$/.test(new URL(e.origin).hostname))return;" +
                "var d=e.data;if(!d||typeof d.fvq!=='string'||!/^\\d{3,4}p$/.test(d.fvq))return;" +
                "var q=document.getElementById('qual');if(q){q.textContent=d.fvq;" +
                "if(typeof d.fvd==='string'&&/^\\d{2,4}x\\d{2,4}$/.test(d.fvd))" +
                "q.title='Decoded: '+d.fvd+' — click to change quality';}" +
                "}catch(x){}});" +
                // Cycle quality manually — driven by the pill click AND the LB/Q shortcut (C#
                // calls window.fvCycleQuality). The request goes into the embed iframe (#p —
                // YT.Player turns the div into the iframe), which clamps it to what's available;
                // the label snaps back to the REAL decoded resolution on the next report, so a
                // declined pick is visible immediately.
                "var _qmodes=['auto','hd720','hd1080','hd1440','hd2160'];" +
                "var _qlabels={auto:'auto',hd720:'720p',hd1080:'1080p',hd1440:'1440p',hd2160:'2160p'};" +
                "var _qi=0;" +
                "window.fvCycleQuality=function(){try{" +
                "_qi=(_qi+1)%_qmodes.length;var m=_qmodes[_qi];" +
                "var f=document.getElementById('p');" +
                "if(f&&f.contentWindow)f.contentWindow.postMessage({fvSet:m},'*');" +
                "var ql=document.getElementById('qual');if(ql)ql.textContent=_qlabels[m];" +
                "if(window.fvShow)fvShow();}catch(x){}};" +
                "document.addEventListener('click',function(e){try{" +
                "if(e.target&&e.target.id==='qual')fvCycleQuality();" +
                "}catch(x){}});" +
                // Progress ticker: update the current / total time labels ~2x/sec. Skips the DOM
                // work while the bottom bar is hidden (fullscreen auto-hide) — the labels aren't
                // visible then, so there's nothing to update.
                "function _fmt(s){s=Math.max(0,Math.floor(s||0));var m=Math.floor(s/60);" +
                "var ss=s%60;return m+':'+(ss<10?'0':'')+ss;}" +
                "setInterval(function(){if(!_botShown||!window.player||!player.getDuration)return;" +
                "var d=player.getDuration()||0,c=player.getCurrentTime()||0;" +
                "var cu=document.getElementById('cur');if(cu)cu.textContent=_fmt(c);" +
                "var to=document.getElementById('tot');if(to&&d>0)to.textContent=_fmt(d);},500);" +
                // Capture keys at the document (capture phase) BEFORE the YouTube iframe sees them,
                // and forward to C# via postMessage. WPF PreviewKeyDown misses keys once focus is
                // inside the WebView2 HWND, so this is the reliable path — it also stops YouTube's
                // own keyboard controls from firing. Only our shortcut keys are intercepted.
                "var _keys={' ':1,'k':1,'K':1,'ArrowLeft':1,'ArrowRight':1,'ArrowUp':1,'ArrowDown':1," +
                "'d':1,'D':1,'f':1,'F':1,'p':1,'P':1,'q':1,'Q':1,'Escape':1};" +
                "document.addEventListener('keydown',function(e){if(_keys[e.key]){e.preventDefault();" +
                "e.stopPropagation();try{chrome.webview.postMessage('key:'+e.key);}catch(x){}}},true);" +
                "</script></body></html>";
        }

        // Script injected into every child document; runs only inside YouTube embed frames.
        // Two jobs: (1) report the ACTUAL playing resolution (video.videoHeight — standard API,
        // updates via the 'resize' event on adaptive switches) to the parent page, which shows
        // it in the bottom bar; (2) when forceHd, request HD via the INTERNAL #movie_player
        // element API (setPlaybackQualityRange) — the approach maintained quality extensions
        // ship today — because the official IFrame quality APIs are no-ops. Defensive by
        // design, playback always wins over quality:
        //   • everything try/caught, every internal call existence-checked → worst case = auto
        //   • bounded polling (~20s max), no infinite loops, never touches playback state
        //   • stall watchdog: 3 genuine buffering stalls (seek-triggered ones filtered) release
        //     the quality lock back to the full adaptive range so the video keeps playing
        internal static string BuildEmbedScript(bool forceHd)
        {
            return
                "(function(){try{" +
                // Only YouTube embed frames — every other document exits immediately.
                "if(!/(^|\\.)youtube(-nocookie)?\\.com$/.test(location.hostname))return;" +
                "if(location.pathname.indexOf('/embed')!==0)return;" +
                "var FORCE=" + (forceHd ? "true" : "false") + ";" +
                // manual = the user picked a quality via the pill; the auto-forcer stands down.
                "var tries=0,stalls=0,released=false,lastSeek=0,manual=false;" +
                // Prefer HD matched to the screen when possible, else fall back toward 1080p:
                // walk the ladder up from 720 remembering the largest AVAILABLE rung, stop at the
                // first one that covers the physical screen (height x DPR). A 4K monitor gets
                // hd2160 when the video has it; a video maxing out at 1080 gets hd1080. No
                // availability data -> the walk stops at the screen-need rung blind. null = auto.
                "function pick(p){try{" +
                "var need=(screen.height||1080)*(window.devicePixelRatio||1);" +
                "var ladder=['hd720','hd1080','hd1440','hd2160'];" +
                "var hs={hd720:720,hd1080:1080,hd1440:1440,hd2160:2160};" +
                "var av=null;" +
                "if(typeof p.getAvailableQualityData==='function'){var d=p.getAvailableQualityData();" +
                "if(d&&d.length){av={};for(var i=0;i<d.length;i++)av[d[i].quality]=1;}}" +
                "var chosen=null;" +
                "for(var j=0;j<ladder.length;j++){var q=ladder[j];" +
                "if(av&&!av[q])continue;" +
                "chosen=q;if(hs[q]>=need)break;}" +
                "return chosen;}catch(e){return null;}}" +
                "function apply(){try{if(!FORCE||released||manual)return;" +
                "var p=document.getElementById('movie_player');" +
                "if(!p||typeof p.setPlaybackQualityRange!=='function')return;" +
                "var q=pick(p);if(q)p.setPlaybackQualityRange(q,q);}catch(e){}}" +
                // Release the lock: restore the full adaptive range so ABR is free again.
                "function release(){released=true;try{" +
                "var p=document.getElementById('movie_player');" +
                "if(p&&typeof p.setPlaybackQualityRange==='function')" +
                "p.setPlaybackQualityRange('tiny','highres');}catch(e){}}" +
                "function arm(){try{" +
                "var v=document.querySelector('video');" +
                "var p=document.getElementById('movie_player');" +
                "if(!v||!p){if(++tries<40)setTimeout(arm,500);return;}" +
                // Report the ACTUAL decoded resolution to the parent page's bar label.
                // videoHeight is 0 until metadata loads; 'resize' fires on every adaptive switch.
                "function report(){try{var h=v.videoHeight||0;if(!h)return;" +
                "window.parent.postMessage({fvq:h+'p',fvd:(v.videoWidth||0)+'x'+h},'*');}catch(e){}}" +
                "v.addEventListener('resize',report);" +
                "v.addEventListener('loadedmetadata',report);" +
                "report();setTimeout(report,1500);" +
                "v.addEventListener('seeking',function(){lastSeek=Date.now();});" +
                // 'waiting' = buffering stall — but seeks fire it too, so ignore those.
                "v.addEventListener('waiting',function(){if(released)return;" +
                "if(Date.now()-lastSeek<2000)return;" +
                "if(++stalls>=3)release();});" +
                "v.addEventListener('canplay',apply);" +
                "apply();setTimeout(apply,2000);" +
                "}catch(e){}}" +
                // Manual quality picks from the parent page's pill (click-to-cycle). 'auto'
                // restores the full adaptive range and stops our forcing; a specific rung is
                // clamped to the nearest available at-or-below the request. Re-arms the stall
                // watchdog so a too-ambitious manual pick still degrades gracefully.
                "window.addEventListener('message',function(e){try{" +
                "if(e.origin!=='https://fullvid.player')return;" +
                "var d=e.data;if(!d||typeof d.fvSet!=='string')return;" +
                "var p=document.getElementById('movie_player');" +
                "if(!p||typeof p.setPlaybackQualityRange!=='function')return;" +
                "stalls=0;manual=true;" +
                // Authoritative correction: if the clamped pick didn't change the decode (e.g.
                // 2160 requested on a 1080-max video), no 'resize' fires and the parent's
                // optimistic label would stick. Report the REAL decode shortly after every pick.
                "setTimeout(function(){try{var vv=document.querySelector('video');" +
                "if(vv&&vv.videoHeight)window.parent.postMessage(" +
                "{fvq:vv.videoHeight+'p',fvd:(vv.videoWidth||0)+'x'+vv.videoHeight},'*');}catch(x){}},800);" +
                // 'auto' = hand control fully back to ABR. Opening the range alone leaves ABR
                // anchored on the current rung, so ALSO ask for 'auto' explicitly (internal API
                // accepts it on current builds); every call individually guarded.
                "if(d.fvSet==='auto'){released=true;" +
                "try{p.setPlaybackQualityRange('auto','auto');}catch(x){" +
                "try{p.setPlaybackQualityRange('tiny','highres');}catch(y){}}" +
                "try{if(typeof p.setPlaybackQuality==='function')p.setPlaybackQuality('auto');}catch(x){}" +
                "return;}" +
                "var order=['hd2160','hd1440','hd1080','hd720'];" +
                "var i=order.indexOf(d.fvSet);if(i<0)return;" +
                "var av=null;" +
                "if(typeof p.getAvailableQualityData==='function'){var dd=p.getAvailableQualityData();" +
                "if(dd&&dd.length){av={};for(var k=0;k<dd.length;k++)av[dd[k].quality]=1;}}" +
                "var q=null;for(;i<order.length;i++){if(!av||av[order[i]]){q=order[i];break;}}" +
                "if(!q)return;released=false;p.setPlaybackQualityRange(q,q);" +
                "}catch(x){}});" +
                "if(document.readyState!=='loading')arm();" +
                "else document.addEventListener('DOMContentLoaded',arm);" +
                "}catch(e){}})();";
        }

        // Minimal HTML-entity escape for the video title (untrusted — comes from yt-dlp JSON).
        internal static string HtmlEscape(string s)
        {
            if (string.IsNullOrEmpty(s)) return string.Empty;
            return s.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;")
                    .Replace("\"", "&quot;").Replace("'", "&#39;");
        }
    }
}
