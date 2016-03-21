// Copyright (c) 2015 Harvard Medical School.  All Rights Reserved

using System;
using System.IO;
using System.IO.Compression;

using tar_cs;

namespace GzTar
{
   public class WriteEventArgs : EventArgs
   {
      public long Written
      {
         get;
         set;
      }

      public WriteEventArgs(long written)
      {
         Written = written;
      }
   }

   public class AGZTar : IDisposable
   {
      FileStream fFileStream = null;
      GZipStream fGzStream = null;
      TarWriter fTarWriter = null;

      public event EventHandler<WriteEventArgs> WriteEvent;

      public AGZTar(string gzTarPath)
      {
         fFileStream = File.Create(gzTarPath);
         fGzStream = new GZipStream(fFileStream, CompressionLevel.Fastest);
         fTarWriter = new TarWriter(fGzStream);

         fTarWriter.WriteEvent += fTarWriter_WriteEvent;
      }

      void fTarWriter_WriteEvent(object sender, tar_cs.WriteEventArgs e)
      {
         RaiseWriteEvent(e.Written);
      }

      public void Write(string filePath)
      {
         if (File.Exists(filePath))
         {
            if (null != fTarWriter)
            {
               fTarWriter.Write(filePath);
            }
         }
      }

      public void Write(string filePath, string asName)
      {
         if (File.Exists(filePath))
         {
            if (null != fTarWriter)
            {
               using (FileStream file = File.OpenRead(filePath))
               {
                  fTarWriter.Write(file, file.Length, asName, 61, 61, 511, File.GetLastWriteTime(file.Name));
               }
            }
         }
      }

      public void Close()
      {
         if (null != fTarWriter)
            fTarWriter.Close();

         if (null != fGzStream)
            fGzStream.Close();

         if (null != fFileStream)
            fFileStream.Close();
      }

      public void Dispose()
      {
         Close();
      }

      protected virtual void RaiseWriteEvent(long written)
      {
         EventHandler<WriteEventArgs> handler = WriteEvent;
         if (null != handler)
         {
            WriteEventArgs args = new WriteEventArgs(written);
            handler(this, args);
         }
      }
   }
}
