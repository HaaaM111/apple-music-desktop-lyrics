using System;
using System.Threading;

static class DummyTarget {
 static int Main(string[] args) {
  if (args.Length == 0 || string.IsNullOrWhiteSpace(args[0])) {
   Thread.Sleep(120000);
   return 0;
  }
  using (var waitEvent = new EventWaitHandle(false, EventResetMode.ManualReset, args[0])) {
   waitEvent.WaitOne(120000);
  }
  return 0;
 }
}
