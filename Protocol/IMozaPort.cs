using System;

namespace MozaPlugin.Protocol
{
    /// <summary>
    /// Transport abstraction for the wheelbase serial link, so the read/write
    /// loops in <see cref="MozaSerialConnection"/> are independent of HOW the
    /// port was opened. Two implementations:
    /// <list type="bullet">
    ///   <item><see cref="SerialPortMozaPort"/> — System.IO.Ports.SerialPort by
    ///   COM name (native Windows).</item>
    ///   <item><see cref="WineByIdMozaPort"/> — a Wine comm handle opened directly
    ///   on a <c>/dev/serial/by-id</c> path (<c>Z:\dev\serial\by-id\…</c>). This
    ///   lets the plugin open the MOZA by USB <i>identity</i> and never blind-open
    ///   an unrelated device (e.g. an Android tablet's CDC-ACM port) — opening that
    ///   under Wine SEGFAULTS the shared wineserver and takes down all of SimHub,
    ///   which out-of-process probe isolation cannot prevent (one wineserver).</item>
    /// </list>
    /// The surface mirrors exactly the <c>SerialPort</c> members the connection
    /// uses (IsConnected, ReadLoop, WriteLoop, Disconnect).
    /// </summary>
    internal interface IMozaPort : IDisposable
    {
        bool IsOpen { get; }
        int BytesToRead { get; }
        int Read(byte[] buffer, int offset, int count);
        void Write(byte[] buffer, int offset, int count);
        void DiscardInBuffer();
        void DiscardOutBuffer();
        void Close();
    }
}
