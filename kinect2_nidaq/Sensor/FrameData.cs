using System;
using Microsoft.Kinect;

namespace Sensor
{

    // class for grabbing frame data

    public class FrameEventArgs : EventArgs
    {

        protected byte[] fByteData;
        protected ushort[] fShortData;

        protected TimeSpan fRelativeTIme;

        protected int fWidth;
        protected int fHeight;
        protected double fTimeStamp;

        /// <summary>
        /// byte array captured from the frame
        /// </summary>
        public byte[] ByteData
        {
            get
            {
                return fByteData;
            }
            set
            {
                fByteData = value;
            }
        }

        /// <summary>
        /// ushort array captured from the frame
        /// </summary>
        public ushort[] ShortData
        {
            get
            {
                return fShortData;
            }

            set
            {
                fShortData = value;
            }
        }

        /// <summary>
        /// frame relative time stamp
        /// </summary>
        public TimeSpan RelativeTime
        {
            get
            {
                return fRelativeTIme;
            }
            set
            {
                fRelativeTIme = value;
            }
        }

        public double TimeStamp
        {
            get
            {
                return fTimeStamp;
            }
            set
            {
                fTimeStamp = value;
            }
        }

        /// <summary>
        /// frame width
        /// </summary>
        public int Width
        {
            get
            {
                return fWidth;
            }
            set
            {
                fWidth = value;
            }
        }

        /// <summary>
        /// frame height
        /// </summary>
        public int Height
        {
            get
            {
                return fHeight;
            }
            set
            {
                fHeight = value;
            }
        }

    }
    public class ColorFrameEventArgs : FrameEventArgs
        {
            public byte[] ColorData
            {
                get
                {
                    return fByteData;
                }
                set
                {
                    fByteData = value;
                }
            }

            public ColorSpacePoint[] ColorSpacepoints
            {
                get;
                set;
            }
            public int DepthWidth
            {
                get;
                set;
            }
            public int DepthHeight
            {
                get;
                set;
            }

        }

    public class DepthFrameEventArgs : FrameEventArgs
        {
            protected ushort fDepthMinReliableDistance = 0;
            protected ushort fDepthMaxReliableDistance = 0;

            public ushort[] DepthData
            {
                get
                {
                    return fShortData;
                }
                set
                {
                    fShortData = value;
                }
            }

            public ushort DepthMinReliableDistance
            {
                get
                {
                    return fDepthMinReliableDistance;
                }
                set
                {
                    fDepthMinReliableDistance = value;
                }
            }

            public ushort DepthMaxReliableDistance
            {
                get
                {
                    return fDepthMaxReliableDistance;
                }
                set
                {
                    fDepthMaxReliableDistance = value;
                }
            }
        }




    }

