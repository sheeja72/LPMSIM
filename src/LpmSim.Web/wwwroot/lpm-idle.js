// LPM SIM — idle session watchdog.
// After IDLE_MS of no user activity (mouse move, mouse down, key press, scroll,
// touch, wheel), navigates the tab to the configured sign-out URL.
//
// The destination depends on the Auth:Mode active on the server. App.razor
// renders <meta name="lpm-signout-url" content="..."> at request time:
//   • Negotiate → "/signed-out"                       (local lock screen)
//   • OIDC      → "/MicrosoftIdentity/Account/SignOut" (true Entra sign-out)
// We read that meta tag so the JS works in both modes without a rebuild.
(function () {
    const IDLE_MS = 5 * 60 * 1000;   // 5 minutes
    let timer = null;

    function signOutUrl() {
        const tag = document.querySelector('meta[name="lpm-signout-url"]');
        return (tag && tag.content) || '/signed-out';
    }

    function signOutNow() {
        // Hard-navigate. Bypasses Blazor SPA routing so the SignalR circuit
        // drops along with any cached state.
        window.location.replace(signOutUrl());
    }

    function reset() {
        if (timer) clearTimeout(timer);
        timer = setTimeout(signOutNow, IDLE_MS);
    }

    // Start the timer immediately, and reset on any sign of life.
    reset();
    ['mousemove', 'mousedown', 'keydown', 'scroll', 'touchstart', 'wheel']
        .forEach(ev => window.addEventListener(ev, reset, { passive: true }));

    // Expose a manual trigger so the Sign Out button can call us directly
    // — same code path, same destination, immediate.
    window.lpmSignOut = signOutNow;
})();
