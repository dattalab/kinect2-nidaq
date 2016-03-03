using System;

namespace Sensor
{
    public class NidaqData
    {

        protected long fTimeStamp;
        protected double[,] fData;

        public double[,] Data
        {
            get
            {
                return fData;
            }
            set
            {
                fData = value;
            }
        }

        public long TimeStamp
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

    }
}
