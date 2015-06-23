using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.IO.Compression;
using System.Net;
using System.Threading.Tasks;

namespace MemTrace
{
  public struct ModuleInfo
  {
    public string Name;
    public ulong BaseAddress;
    public ulong SizeBytes;
  }

  public struct TraceMark
  {
    public string Name;
    public ulong TimeStamp;

    public override string ToString()
    {
      return Name;
    }
  }

  public interface ITraceFileHandler
  {
    object OnRecordingStarted(string filename, TraceMeta meta, TraceConverter recorder);
    void OnRecordingProgress(object context, TraceMeta meta);
    void OnRecordingEnded(object context);
  }

  public class TraceConverter : IDisposable
  {
    protected readonly string m_FileName;
    protected FileStream m_OutputFile;
    protected object m_Lock = new object();
    internal readonly TraceTranscoder m_Analyzer;
    protected readonly ITraceFileHandler m_Handler;
    protected object m_Context;
    protected byte[] m_Buffer = new byte[65536];

    public TraceConverter(ITraceFileHandler handler, string output_filename)
    {
      m_Handler = handler;
      m_FileName = output_filename;
      m_OutputFile = new FileStream(output_filename, FileMode.Create, FileAccess.Write);

      m_Analyzer = new TraceTranscoder(m_OutputFile);

      m_Analyzer.MetaData.Status = TraceStatus.Recording;
      m_Analyzer.MetaData.SourceMachine = "0.0.0.0";

      m_Context = handler.OnRecordingStarted(output_filename, m_Analyzer.MetaData, this);
    }

    ~TraceConverter()
    {
      Dispose(false);
    }

    public void Dispose()
    {
      Dispose(true);
    }

    internal virtual void Dispose(bool disposing)
    {
      if (m_OutputFile != null)
      {
        m_OutputFile.Dispose();
        m_OutputFile = null;
      }

      if (disposing)
      {
        GC.SuppressFinalize(this);
      }
    }

    internal virtual void OnEndOfInput()
    {

      m_Analyzer.Flush();
      m_OutputFile.Close();

      m_Handler.OnRecordingProgress(m_Context, m_Analyzer.MetaData);
      m_Handler.OnRecordingEnded(m_Context);
    }

    public virtual void Cancel()
    {
    }

    public virtual void AddTraceMarkFromUI()
    {
    }
  }

  public class TraceRecorder : TraceConverter
  {
    private Socket m_Socket;

    public TraceRecorder(ITraceFileHandler handler, Socket input, string output_filename)
     : base(handler, output_filename)
    {

      m_Socket = input;
      m_Context = handler.OnRecordingStarted(output_filename, m_Analyzer.MetaData, this);

      m_Socket.BeginReceive(m_Buffer, 0, m_Buffer.Length, SocketFlags.None, OnDataRead, null);
    }

    ~TraceRecorder()
    {
      Dispose(false);
    }

    internal override void Dispose(bool disposing)
    {
      lock (m_Lock)
      {
        if (m_Socket != null)
        {
          m_Socket.Close();
          m_Socket.Dispose();
          m_Socket = null;
        }
      }

      base.Dispose(disposing);
    }

    void OnDataRead(IAsyncResult res)
    {
      int bytes_read = 0;

      try
      {
        bytes_read = m_Socket.EndReceive(res);
      }
      catch (SocketException ex)
      {
        Debug.WriteLine("Exception: {0}", ex.Message);
        // Ignore - treat as EOF.
      }
      catch (EndOfStreamException ex)
      {
        Debug.WriteLine("Exception: {0}", ex.Message);
        // Ignore - owner has forcibly closed the socket because we're shutting down.
      }
      catch (ObjectDisposedException ex)
      {
        Debug.WriteLine("Exception: {0}", ex.Message);
        // Ignore - owner has forcibly closed the socket because we're shutting down.
      }
      catch (Exception ex)
      {
        Debug.WriteLine("Exception: {0}", ex.Message);
      }

      m_Analyzer.Update(m_Buffer, bytes_read);

      m_Handler.OnRecordingProgress(m_Context, m_Analyzer.MetaData);

      if (0 == bytes_read || !m_Socket.Connected)
      {
        OnEndOfInput();
      }
      else
      {
        m_Socket.BeginReceive(m_Buffer, 0, m_Buffer.Length, SocketFlags.None, OnDataRead, null);
      }
    }

    internal override void OnEndOfInput()
    {
      if (m_Socket != null)
      {
        m_Socket.Close();
      }
    }

    public override void Cancel()
    {
      m_Socket.Close();
    }

    public override void AddTraceMarkFromUI()
    {
      m_Analyzer.MetaData.AddMark(new TraceMark { Name = "UI Mark", TimeStamp = m_Analyzer.CurrentTimeStamp });
    }
  }

  public class TraceDumpConverter : TraceConverter
  {
    Task filereader;
    CancellationTokenSource cancelSource;
    string m_dumpfile;

    public TraceDumpConverter(ITraceFileHandler handler, string dumpfile, string output_filename)
     : base(handler, output_filename)
    {
      m_dumpfile = dumpfile;
      cancelSource = new CancellationTokenSource();

      var token = cancelSource.Token;
      filereader = Task.Run(() => Readloop(token), token);
    }

    private void Readloop(CancellationToken cancelToken)
    {
      FileStream steam = new FileStream(m_dumpfile, FileMode.Open, FileAccess.Read);

      try
      {
        int bytes_read = 0;

        while ((bytes_read = steam.Read(m_Buffer, 0, m_Buffer.Length)) != 0)
        {
          m_Analyzer.Update(m_Buffer, bytes_read);
          m_Handler.OnRecordingProgress(m_Context, m_Analyzer.MetaData);

          cancelToken.ThrowIfCancellationRequested();
        }      
      }
      finally
      {
        steam.Close();
        OnEndOfInput();
      } 
    }

    public override void Cancel()
    {
      cancelSource.Cancel();
    }
  }
}
