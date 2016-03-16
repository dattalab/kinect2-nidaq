using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Metadata
{
    public class kMetadata
    {
        // everything we want to save to JSON from each session

        public string SubjectName
        {
            get;
            set;
        }

        public string SessionName
        {
            get;
            set;
        }

        public int NidaqChannels
        {
            get;
            set;
        }

        public string[] NidaqChannelNames
        {
            get;
            set;
        }

        public string NidaqTerminalConfiguration
        {
            get;
            set;
        }

        public String NidaqDataType
        {
            get;
            set;
        }

        public int[] DepthResolution
        {
            get;
            set;
        }

        public Boolean IsLittleEndian
        {
            get;
            set;
        }

        public String DepthDataType
        {
            get;
            set;
        }



        public int[] ColorResolution
        {
            get;
            set;
        }

        public String ColorDataType
        {
            get;
            set;
        }

        public DateTime StartTime
        {
            get;
            set;
        }


       
        

       

    }
}
