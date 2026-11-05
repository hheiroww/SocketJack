mergeInto(LibraryManager.library, {
  SJBrowserVoice_AllowFakeCapture: function() {
    return window.__socketJackVoiceAcceptanceFakeMic === true && (location.hostname === '127.0.0.1' || location.hostname === 'localhost') ? 1 : 0;
  },
  SJBrowserVoice_Result: function(pointer) {
    var result = UTF8ToString(pointer); document.body.setAttribute('data-socketjack-voice-probe', result); console.log(result);
  }
});
