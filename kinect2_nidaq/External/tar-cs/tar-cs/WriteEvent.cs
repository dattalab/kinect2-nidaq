using System;

namespace tar_cs
{
   public class WriteEventArgs : EventArgs
   {
      public long Written { get; set; }

      public WriteEventArgs(long written)
      {
         Written = written;
      }
   }
}
