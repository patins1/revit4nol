using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Data;
using System.Drawing;
using System.Linq;
using System.Text;
using System.Windows.Forms;

namespace BIMTools
{
    public partial class ObjectInspector : Form
    {

        public PropertyGrid PropertyGrid { get {return propertyGrid1; } }
        public NationalObjectLibraryClass nationalObjectLibraryClass;
        public CustomClass myProperties = new CustomClass();

        public ObjectInspector()
        {
            InitializeComponent();
            PropertyGrid.SelectedObject = myProperties;
        }

        private void timer1_Tick(object sender, EventArgs e)
        {
            if (nationalObjectLibraryClass != null) nationalObjectLibraryClass.updateObjectPropertiesPage();
        }

        private void button1_Click(object sender, EventArgs e)
        {
            nationalObjectLibraryClass.showBrowser();
        }

        private void ObjectInspector_FormClosing(object sender, FormClosingEventArgs e)
        {
            e.Cancel = true;
        }

        private void button2_Click(object sender, EventArgs e)
        {
            nationalObjectLibraryClass.selectAll();
        }

    }
}
