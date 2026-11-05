mergeInto(LibraryManager.library, {
  SJBrowserProbe_Result: function(pointer) {
    var value = UTF8ToString(pointer);
    document.body.setAttribute('data-socketjack-probe', value);
    console.log('SOCKETJACK_BROWSER_' + value);
  }
});
