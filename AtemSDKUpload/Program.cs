using BMDSwitcherAPI;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace AtemSDKUpload
{
    public sealed class TransferCompleteCallback : IBMDSwitcherStillsCallback
    {
        private readonly Action<IBMDSwitcherFrame> _action;
        private readonly int _index;

        public TransferCompleteCallback(Action<IBMDSwitcherFrame> action, int index)
        {
            _action = action;
            _index = index;
        }

        public void Notify(_BMDSwitcherMediaPoolEventType eventType, IBMDSwitcherFrame frame, int index)
        {
            if (index != _index)
                return;

            switch (eventType)
            {
                case _BMDSwitcherMediaPoolEventType.bmdSwitcherMediaPoolEventTypeTransferCompleted:
                    _action(frame);
                    break;
                case _BMDSwitcherMediaPoolEventType.bmdSwitcherMediaPoolEventTypeTransferCancelled:
                    _action(null);
                    break;
                case _BMDSwitcherMediaPoolEventType.bmdSwitcherMediaPoolEventTypeTransferFailed:
                    _action(null);
                    break;
            }
        }
    }

    public sealed class LockCallback : IBMDSwitcherLockCallback
    {
        private readonly Action action;

        public LockCallback(Action action)
        {
            this.action = action;
        }

        public void Obtained()
        {
            action();
        }
    }

    class Program
    {
        private IBMDSwitcherDiscovery m_switcherDiscovery;
        private IBMDSwitcher m_switcher;
        private IBMDSwitcherMediaPool m_mediaPool;

        private void Run()
        {
            m_switcherDiscovery = new CBMDSwitcherDiscovery();
            if (m_switcherDiscovery == null)
            {
                WaitForExit("Could not create Switcher Discovery Instance.\nATEM Switcher Software may not be installed.");
                return;
            }

            _BMDSwitcherConnectToFailure failReason = 0;
            string address = "10.42.13.99";

            try
            {
                // Note that ConnectTo() can take several seconds to return, both for success or failure,
                // depending upon hostname resolution and network response times, so it may be best to
                // do this in a separate thread to prevent the main GUI thread blocking.
                m_switcherDiscovery.ConnectTo(address, out m_switcher, out failReason);
            }
            catch (COMException)
            {
                // An exception will be thrown if ConnectTo fails. For more information, see failReason.
                switch (failReason)
                {
                    case _BMDSwitcherConnectToFailure.bmdSwitcherConnectToFailureNoResponse:
                        WaitForExit("No response from Switcher");
                        break;
                    case _BMDSwitcherConnectToFailure.bmdSwitcherConnectToFailureIncompatibleFirmware:
                        WaitForExit("Switcher has incompatible firmware");
                        break;
                    default:
                        WaitForExit("Connection failed for unknown reason");
                        break;
                }
                return;
            }

            Console.WriteLine("Connected");


            m_mediaPool = m_switcher as IBMDSwitcherMediaPool;
            if (m_mediaPool == null)
            {
                WaitForExit("Failed to cast to media pool");
                return;
            }


            m_mediaPool.CreateFrame(_BMDSwitcherPixelFormat.bmdSwitcherPixelFormat10BitYUVA, 1920, 1080, out IBMDSwitcherFrame frame);
            if (frame == null)
            {
                WaitForExit("Failed to create frame");
                return;
            }

            frame.GetBytes(out IntPtr buffer);
            byte[] frameData = RandomFrame();
            Marshal.Copy(frameData, 0, buffer, 1920 * 1080 * 4);

            Stopwatch sw = new Stopwatch();

            uint max_index = 32;
            uint index = 0;
            while (true)
            { 
                sw.Restart();
                UploadStillSdk(index % max_index, "frame " + index, frame);
                sw.Stop();

                Console.WriteLine("Upload outer #{0} took {1}ms", index, sw.ElapsedMilliseconds);

                Thread.Sleep(100);
                
                index++;
            }


            // End
            WaitForExit();
        }

        public static byte[] RandomFrame()
        {
            var r = new Random();
            byte[] b = new byte[1920 * 1080 * 4];
            r.NextBytes(b);
            return b;
        }

        void UploadStillSdk(uint index, string name, IBMDSwitcherFrame frame)
        {
            m_mediaPool.GetStills(out IBMDSwitcherStills stills);
            if (stills == null)
            {
                WaitForExit("Failed to get stills pool");
                return;
            }

            // Wait for lock
            var evt = new AutoResetEvent(false);
            var cb = new LockCallback(() => { evt.Set(); });
            stills.Lock(cb);
            if (!evt.WaitOne(TimeSpan.FromSeconds(3)))
            {
                WaitForExit("Timed out getting lock");
                return;
            }
            var cb2 = new TransferCompleteCallback(fr =>
            {
                if (fr != null)
                {
                    stills.Unlock(cb);
                    evt.Set();
                }
            }, (int)index);
            stills.AddCallback(cb2);

            Stopwatch sw = new Stopwatch();

            evt.Reset();
            sw.Start();
            stills.Upload(index, name, frame);

            if (!evt.WaitOne(TimeSpan.FromSeconds(10)))
            {
                WaitForExit("Timed out doing upload");
                return;
            }

            sw.Stop();

            Console.WriteLine("Upload inner #{0} took {1}ms", index, sw.ElapsedMilliseconds);

            stills.RemoveCallback(cb2);
        }

        private void WaitForExit(string msg=null)
        {
            if (msg != null &&  msg.Length != 0)
                Console.WriteLine(msg);

            Console.WriteLine("Press enter to exit...");
            Console.ReadLine();
            Environment.Exit(1);
        }

        static void Main(string[] args)
        {
            var pgm = new Program();
            pgm.Run();
        }
    }
}
