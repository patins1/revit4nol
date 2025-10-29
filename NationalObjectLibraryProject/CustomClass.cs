using System;
using System.Collections;
using System.Collections.Generic;
using System.ComponentModel;
using System.Text.RegularExpressions;

namespace BIMTools
{
    /// <summary>
    /// CustomClass (Which is binding to property grid)
    /// </summary>
    public class CustomClass : CollectionBase, ICustomTypeDescriptor
    {
        /// <summary>
        /// Add CustomProperty to Collectionbase List
        /// </summary>
        /// <param name="Value"></param>
        public void Add(CustomProperty Value)
        {
            base.List.Add(Value);
        }

        /// <summary>
        /// Remove item from List
        /// </summary>
        /// <param name="Name"></param>
        public void Remove(string Name)
        {
            foreach (CustomProperty prop in base.List)
            {
                if (prop.Name == Name)
                {
                    base.List.Remove(prop);
                    return;
                }
            }
        }

        /// <summary>
        /// Indexer
        /// </summary>
        public CustomProperty this[int index]
        {
            get
            {
                return (CustomProperty)base.List[index];
            }
            set
            {
                base.List[index] = (CustomProperty)value;
            }
        }


        #region "TypeDescriptor Implementation"
        /// <summary>
        /// Get Class Name
        /// </summary>
        /// <returns>String</returns>
        public String GetClassName()
        {
            return TypeDescriptor.GetClassName(this, true);
        }

        /// <summary>
        /// GetAttributes
        /// </summary>
        /// <returns>AttributeCollection</returns>
        public AttributeCollection GetAttributes()
        {
            return TypeDescriptor.GetAttributes(this, true);
        }

        /// <summary>
        /// GetComponentName
        /// </summary>
        /// <returns>String</returns>
        public String GetComponentName()
        {
            return TypeDescriptor.GetComponentName(this, true);
        }

        /// <summary>
        /// GetConverter
        /// </summary>
        /// <returns>TypeConverter</returns>
        public TypeConverter GetConverter()
        {
            return TypeDescriptor.GetConverter(this, true);
        }

        /// <summary>
        /// GetDefaultEvent
        /// </summary>
        /// <returns>EventDescriptor</returns>
        public EventDescriptor GetDefaultEvent()
        {
            return TypeDescriptor.GetDefaultEvent(this, true);
        }

        /// <summary>
        /// GetDefaultProperty
        /// </summary>
        /// <returns>PropertyDescriptor</returns>
        public PropertyDescriptor GetDefaultProperty()
        {
            return TypeDescriptor.GetDefaultProperty(this, true);
        }

        /// <summary>
        /// GetEditor
        /// </summary>
        /// <param name="editorBaseType">editorBaseType</param>
        /// <returns>object</returns>
        public object GetEditor(Type editorBaseType)
        {
            return TypeDescriptor.GetEditor(this, editorBaseType, true);
        }

        public EventDescriptorCollection GetEvents(Attribute[] attributes)
        {
            return TypeDescriptor.GetEvents(this, attributes, true);
        }

        public EventDescriptorCollection GetEvents()
        {
            return TypeDescriptor.GetEvents(this, true);
        }

        public PropertyDescriptorCollection GetProperties(Attribute[] attributes)
        {
            List<PropertyDescriptor> newProps = new List<PropertyDescriptor>();
            for (int i = 0; i < this.Count; i++)
            {
                CustomProperty prop = (CustomProperty)this[i];
                if (prop.Visible)
                    newProps.Add(new CustomPropertyDescriptor(ref prop, attributes));
            }

            return new PropertyDescriptorCollection(newProps.ToArray());
        }

        public PropertyDescriptorCollection GetProperties()
        {
            return TypeDescriptor.GetProperties(this, true);
        }

        public object GetPropertyOwner(PropertyDescriptor pd)
        {
            return this;
        }
        #endregion

    }

    /// <summary>
    /// Custom property class 
    /// </summary>
    public class CustomProperty
    {
        private string sName = string.Empty;
        private bool bReadOnly = false;
        private bool bMustChange = false;
        private string bRegularExpression = null;
        private bool bVisible = true;
        protected object objValue = null;
        protected object oriValue = null;
        protected string sCategory = string.Empty;
        protected string sDescription = string.Empty;
        protected TypeConverter sTypeConverter = null;
        private int iLodMustChange;
        private int iLodReadOnly;
        private int iLodVisibility;

        public CustomProperty(string sName, object value, object oriValue, Type type, bool bReadOnly, bool bVisible, string sCategory, string sDescription)
        {
            this.sName = sName;
            this.objValue = value;
            this.oriValue = oriValue;
            this.type = type;
            this.bReadOnly = bReadOnly;
            this.bVisible = bVisible;
            this.sCategory = sCategory;
            if (sDescription != null)
                this.sDescription = sDescription;
            else
                this.sDescription = sName;
        }

        private Type type;
        public Type Type
        {
            get { return type; }
        }

        public bool ReadOnly
        {
            get
            {
                return bReadOnly;
            }
            set
            {
                bReadOnly = value;
            }
        }

        public bool MustChange
        {
            get
            {
                return bMustChange;
            }
            set
            {
                bMustChange = value;
            }
        }

        public int LodReadOnly
        {
            get
            {
                return iLodReadOnly;
            }
            set
            {
                iLodReadOnly = value;
            }
        }

        public int LodMustChange
        {
            get
            {
                return iLodMustChange;
            }
            set
            {
                iLodMustChange = value;
            }
        }

        public int LodVisibility
        {
            get
            {
                return iLodVisibility;
            }
            set
            {
                iLodVisibility = value;
            }
        }

        public string RegularExpression
        {
            get
            {
                return bRegularExpression;
            }
            set
            {
                bRegularExpression = value;
            }
        }

        public string Name
        {
            get
            {
                return sName;
            }
        }

        public bool Visible
        {
            get
            {
                return bVisible;
            }
            set
            {
                bVisible = value;
            }
        }

        virtual public object Value
        {
            get
            {
                return objValue;
            }
            set
            {
                objValue = value;
            }
        }

        virtual public object OriginalValue
        {
            get
            {
                return oriValue;
            }
        }

        public string Category
        {
            get
            {
                return sCategory;
            }
        }

        public string Description
        {
            get
            {
                return sDescription;
            }
        }

        public string ErrorText
        {
            get
            {
                if (MustChange && Value != null && Value.Equals(OriginalValue))
                    return "VALUE MUST BE EDITED!";
                if (RegularExpression != null && !Regex.IsMatch("" + Value, RegularExpression))
                    return "NOT MATCHES REGULAR EXPRESSION " + RegularExpression;
                return null;
            }
        }


        public TypeConverter TypeConverter
        {
            get
            {
                return sTypeConverter;
            }
        }

    }


    /// <summary>
    /// Custom PropertyDescriptor
    /// </summary>
    public class CustomPropertyDescriptor : PropertyDescriptor
    {
        CustomProperty m_Property;
        public CustomPropertyDescriptor(ref CustomProperty myProperty, Attribute[] attrs)
            : base(myProperty.Name, attrs)
        {
            m_Property = myProperty;
        }

        #region PropertyDescriptor specific

        public override bool CanResetValue(object component)
        {
            return ShouldSerializeValue(component);
        }

        public override Type ComponentType
        {
            get { return m_Property.Type; }
        }


        public override TypeConverter Converter
        {
            get
            {
                if (m_Property.TypeConverter != null)
                    return m_Property.TypeConverter;
                return base.Converter;
            }
        }

        public override object GetValue(object component)
        {
            return m_Property.Value;
        }

        public override string Description
        {
            get
            {
                string err = m_Property.ErrorText;
                if (err != null)
                    return err;
                return m_Property.Description; 
            }
        }

        public override string Category
        {
            get { return m_Property.Category; }
        }

        public override string DisplayName
        {
            get
            {
                if (m_Property.MustChange && !ShouldSerializeValue(null))
                    return ">>> " + m_Property.Name;
                if (m_Property.RegularExpression != null && !Regex.IsMatch(""+m_Property.Value, m_Property.RegularExpression))
                    return ">>> " + m_Property.Name;
                return m_Property.Name; 
            }
        }

        public override bool IsReadOnly
        {
            get { return m_Property.ReadOnly; }
        }

        public override bool IsBrowsable
        {
            get { return m_Property.Visible; }
        }

        public override void ResetValue(object component)
        {
            m_Property.Value = m_Property.OriginalValue;
        }

        public override bool ShouldSerializeValue(object component)
        {
            return !(m_Property.Value != null && m_Property.Value.Equals(m_Property.OriginalValue));
        }

        public override void SetValue(object component, object value)
        {
            m_Property.Value = value;
        }

        public override Type PropertyType
        {
            get { return m_Property.Type; }
        }

        #endregion


    }

    public class ComboConverter : StringConverter
    {
        string[] items;

        public ComboConverter(string[] items)
        {
            this.items = items;
        }

        public override bool GetStandardValuesSupported(
                               ITypeDescriptorContext context)
        {
            return true;
        }

        public override StandardValuesCollection
                       GetStandardValues(ITypeDescriptorContext context)
        {
            return new StandardValuesCollection(items);
        }
        public override bool GetStandardValuesExclusive(ITypeDescriptorContext context)
        {
            return true; // True will limit to list. false will show the list, but allow free-formentry
        }
    }

}
