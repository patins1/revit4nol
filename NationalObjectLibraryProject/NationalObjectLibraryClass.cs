using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Web.Script.Serialization;
using Autodesk.Revit.ApplicationServices;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Autodesk.Revit.UI.Selection;
using Ini;
using System.ComponentModel;
using System.Windows.Interop;
using System.Globalization;
using System.Reflection;
using System.Collections;
using System.Runtime.InteropServices;

namespace BIMTools
{

    public delegate void ExternalNOLRequest();
    
    public class RequestHandler : IExternalEventHandler
    {
        public List<ExternalNOLRequest> requests = new List<ExternalNOLRequest>();

        public void Execute(UIApplication uiapp)
        {
            while (requests.Count > 0)
            {
                ExternalNOLRequest request = requests.First();
                request();
                requests.Remove(request);
            }
        }

        public String GetName()
        {
            return "NOL External Event";
        }

    }


    [TransactionAttribute(TransactionMode.Manual)]
    [RegenerationAttribute(RegenerationOption.Manual)]
    public class NationalObjectLibraryClass : IExternalCommand, IBrowserCallback
    {
        // If an extrusion is not created with a depth below this constant, Revit will throw an error.
        // Actually the correct value seems to be 0.005208333333333333333.. which is 1/192
        const double SMALLEST_EXTRUSION_DEPTH = 0.005208333333333334;

        const string NOLClient = "C:\\bim\\NOLClient\\";
        const string NOLServer = "C:\\bim\\NOLServer\\";
        const String iniPath = NOLClient + "config.ini";

        bool polygonAsExtrusion = true;
        bool polygonAsForm = false;

        IniFile importIni;

        static BrowserForm dlg;
        static public ObjectInspector objectInspector;
        ExternalEvent m_ExEvent;
        RequestHandler m_Handler;

        // Application of Revit
        private Autodesk.Revit.ApplicationServices.Application m_revit;
        UIApplication uiApplication;

        private int totalPrimitives = 0;
        private int totalExtrusions = 0;
        private int preparsedTotalExtrusions = 0;
        private int preparsedTotalPolyloops = 0;
        private int totalPolyloops = 0;
        private int errorneousExtrusions = 0;
        private int errorneousPolyloops = 0;
        private int errorneousUnknownTypes = 0;
        private string oneUnknownType = null;

        private int totalElements = 0;
        private int familyInstances = 0;
        private int nolFamilyInstances = 0;

        Document doc;
        IList<Element> elems;
        Category targetCat;
        Dictionary<string, Element> guidMap;

        double minZ;
        double maxZ;
        double minY;
        double maxY;
        double minX;
        double maxX;
        XYZ offs;
        ReferencePlane sill;
        ReferencePlane memberLeft, memberRight, startPlaneY, endPlaneY, startPlaneZ, endPlaneZ, centerLeftRight, planeLeft,planeRight,planeFront,planeBack;
        Level levelUpperRefLevel, levelLowerRefLevel;
        View pView, viewLeft, pViewLowerRefLevel, pViewRefLevel;
        //SketchPlane skplane;
        Boolean isStructuralFraming, isColumn;

        private bool silentMode;
        long lastMeasure = DateTime.Now.Ticks;

        Category m_subCat;
        Dictionary<string, Category> materialMap;
        String m_symbolName;
        String lastWrittenFamilyFileName;

        [DllImport("user32.dll")]
        static extern bool SetForegroundWindow(IntPtr hWnd);

        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            //http://spiderinnet.typepad.com/blog/2011/05/parameter-of-revit-api-31-create-project-parameter.html
            //http://usa.autodesk.com/adsk/servlet/index?siteID=123112&id=16849339
            //http://social.msdn.microsoft.com/Forums/en-US/wpf/thread/cdd2d256-101b-44ef-93d2-d6617d419db0
            //http://thebuildingcoder.typepad.com/blog/2011/06/creating-and-inserting-an-extrusion-family.html
            //http://spiderinnet.typepad.com/blog/2011/10/use-familymanageraddparameter-with-vbnet.html
            //http://whatrevitwants.blogspot.com.au/2011/10/create-component-family-with-category.html
            //http://thebuildingcoder.typepad.com/blog/2010/06/ifc-guid-algorithm-in-c.html
            //http://thebuildingcoder.typepad.com/blog/2009/02/uniqueid-dwf-and-ifc-guid.html
            //http://stackoverflow.com/questions/1387755/can-javascriptserializer-exclude-properties-with-null-default-values

            //Get application and document objects
            uiApplication = commandData.Application;
            m_revit = uiApplication.Application;
            importIni = new IniFile(NOLClient + @"revit\import.ini");


            try
            {

            // A new handler to handle request posting by the dialog
            m_Handler = new RequestHandler();

            // External Event for the dialog to use (to post requests)
            m_ExEvent = ExternalEvent.Create(m_Handler);
                if (objectInspector == null || objectInspector.IsDisposed)
                {
                    objectInspector = new ObjectInspector();
                    objectInspector.nationalObjectLibraryClass = this;
                    System.Windows.Forms.IWin32Window revit_window = new JtWindowHandle(Autodesk.Windows.ComponentManager.ApplicationWindow);
                    objectInspector.Show(revit_window);
                }
                objectInspector.nationalObjectLibraryClass = this;
                updateObjectPropertiesPage();
                showBrowser();
                return Result.Succeeded;
            }
            //If the user right-clicks or presses Esc, handle the exception
            catch (Autodesk.Revit.Exceptions.OperationCanceledException)
            {
                return Result.Cancelled;
            }
            //Catch other errors
            catch (Exception ex)
            {
                message = ex.ToString();
                System.IO.File.WriteAllText(NOLClient+@"debug\ErrorMessageMain.txt", message);
                return Result.Failed;
            }

        }

        public void showBrowser()
        {
            if (dlg != null)
            {
                if (dlg.Visible)
                {
                    dlg.BringToFront();
                    return;
                }
                dlg.Dispose();
                dlg = null;
            }

            String RAASCMSURL = new IniFile(NOLServer + "config.ini").IniReadValue("MAIN", "RAASCMSURL");
            if (RAASCMSURL == "")
            {
                RAASCMSURL = new IniFile(iniPath).IniReadValue("MAIN", "RAASCMSURL");
                if (RAASCMSURL == "")
                {
                    TaskDialog.Show("National Object Library", "RAASCMSURL not defined in " + iniPath + "!");
                    return;
                }
            }


            dlg = new BrowserForm();
            dlg.browserCallback = this;
            string userName = System.Security.Principal.WindowsIdentity.GetCurrent().Name;
            dlg.getWebBrowser().Navigate(RAASCMSURL + "?username=" + Uri.EscapeUriString(userName) + "&ieversion=" + Uri.EscapeUriString(dlg.getWebBrowser().Version.ToString()) + "&osversion=" + Uri.EscapeUriString(System.Environment.OSVersion.VersionString) + "&cadtool=" + Uri.EscapeUriString(m_revit.VersionName + "." + m_revit.VersionNumber + "." + m_revit.VersionBuild) + "&ownversion=1");
            dlg.Show();
        }

        public ObjectLibraryResponse getBackup(Element elem)
        {
            Parameter param = elem.get_Parameter("NOLBackup");
            string json = param != null ? GetParameterInformation(param, doc) : null;
            if (json != null && json.Length > 0)
            {
                return getDeserializer().Deserialize<ObjectLibraryResponse>(json);
            }
            return null;
        }

        public void updateObjectPropertiesPage()
        {
            try
            {
                IList<Element> prevElems = elems;
                associateContext(false);
                if (prevElems != null && prevElems.Count == elems.Count)
                {
                    bool isUnequal = false;
                    for (int i = 0; i < prevElems.Count; i++)
                    {
                        Element e1 = prevElems.ElementAt(i);
                        Element e2 = elems.ElementAt(i);
                        if (!e1.Id.Equals(e2.Id))
                        {
                            isUnequal = true;
                            break;
                        }
                    }
                    if (!isUnequal)
                        return;
                } 

                HashSet<string> intersectedProps = null;
                Element luckyOne = null;
                List<string> propNames = null;
                Dictionary<string, ProductLine> backupValues = new Dictionary<string, ProductLine>();

                foreach (Element elem in elems)
                {
                    ObjectLibraryResponse foo = getBackup(elem);
                    if (foo != null)
                    {
                        propNames = new List<string>();
                        foreach (ProductLine line in foo.productLineProperties)
                        {
                            propNames.Add(line.name);
                            backupValues.Add(line.name, line);
                        }
                        if (intersectedProps == null)
                        {
                            intersectedProps = new HashSet<string>(propNames);
                        }
                        else
                        {
                            intersectedProps.IntersectWith(propNames);
                        }
                        luckyOne = elem;
                    }
                }
                objectInspector.myProperties.Clear();
                if (luckyOne != null)
                {
                    loadProperties(luckyOne, propNames, intersectedProps, backupValues, objectInspector.myProperties);
                }
                validateLOD();
            }
            catch (Exception ex)
            {
                String message = ex.ToString();
                System.IO.File.WriteAllText(NOLClient + @"debug\ErrorMessage.txt", message);
            }
        }

        void loadProperties(Element luckyOne, List<string> propNames, HashSet<string> intersectedProps, Dictionary<string, ProductLine> backupValues, CustomClass myProperties)
        {
            int addedParams = 0;
            foreach (String name in propNames)
            {
                Parameter line = luckyOne.get_Parameter(name);
                int i = name.IndexOf(".");
                if (line != null && i != -1 && intersectedProps.Contains(name))
                {
                    string psetName = name.Substring(0, i);
                    string propName = name.Substring(i + 1);
                    ProductLine backupValue = null;
                    backupValues.TryGetValue(name, out backupValue);
                    var prop = new NOLProperty(propName, GetParameterInformation(line, doc), backupValue.value, typeof(string), true, psetName, null, null, this);
                    prop.LodMustChange = backupValue.lodMustChange;
                    prop.LodReadOnly = backupValue.lodReadOnly;
                    prop.LodVisibility = backupValue.lodVisibility;
                    myProperties.Add(prop);
                    addedParams++;
                }
            }
        }

        static private string withoutComma(string p)
        {
            if (p == null) return "NULL STRING";
            return p.Replace(',', ' ');
        }

        private JavaScriptSerializer getSerializer()
        {
            JavaScriptSerializer ser = new JavaScriptSerializer();
            // allow up to 200mb
            ser.MaxJsonLength = 200 * 1024 * 1024;
            ser.RegisterConverters(new JavaScriptConverter[] { new NullPropertiesConverter() });
            return ser;
        }

        private JavaScriptSerializer getDeserializer()
        {
            JavaScriptSerializer ser = new JavaScriptSerializer(new ManualResolver());
            // allow up to 200mb
            ser.MaxJsonLength = 200 * 1024 * 1024;
            return ser;
        }

        public void doRequest(ExternalNOLRequest request)
        {
            m_Handler.requests.Add(request);
            m_ExEvent.Raise();
        }

        public void updateProperty(string name, string value)
        {
            doRequest(() =>
            {

                associateContext(false);
                Transaction trans = new Transaction(doc);
                trans.Start("updateProperty");
                foreach (Element elem in elems)
                {
                    Parameter para = elem.get_Parameter(name);
                    if (para != null)
                    {
                        para.Set(value);
                    }
                }
                trans.Commit();
                if (name.EndsWith(".LOD"))
                {
                    validateLOD();
                }
                NationalObjectLibraryClass.logProperties(NationalObjectLibraryClass.objectInspector.myProperties);

            });
        }

        public String getCurrentProperties(String flags)
        {

            try
            {
                bool fromAllObjects = flags.Contains("fromAllObjects");
                bool exportIfc = flags.Contains("exportIfc");
                bool generateIfc = flags.Contains("generateIfc");
                bool exportProprietary = flags.Contains("exportProprietary");
                associateContext(fromAllObjects);

                ObjectLibraryResponse fooInput = new ObjectLibraryResponse();
                if (targetCat != null)
                    fooInput.productLineType = targetCat.Name;
                else
                    fooInput.productLineType = "TYPE_NOT_SET";

                totalElements = 0;
                if (!generateIfc)
                foreach (Element elem in elems)
                {
                    ObjectLibraryResponse foo = getBackup(elem);
                    if (foo != null)
                    {
                        Dictionary<string, ProductLine> backupValues = new Dictionary<string, ProductLine>();
                        foreach (ProductLine line in foo.productLineProperties)
                        {
                            backupValues.Add(line.name, line);
                        }
                        bool guidAdded = false;
                        foreach (Parameter para in elem.Parameters)
                        {
                            string name = para.Definition.Name;
                            string value = GetParameterInformation(para, doc);
                            if (backupValues.Keys.Contains(name) && backupValues[name].value != value)
                            {
                                if (!guidAdded)
                                {
                                    fooInput.productLineProperties.Add(new ProductLine("#GUID", getIfcGuid(elem)));
                                    guidAdded = true;
                                    totalElements++;
                                }
                                ProductLine prop = (ProductLine) backupValues[name].Clone();
                                prop.value = value;
                                fooInput.productLineProperties.Add(prop);   
                            }
                        }
                    }
                    if (!fromAllObjects)
                        break;
                }
                fooInput.productLineProperties.Add(new ProductLine("#TotalElements", "" + totalElements));
                //fooInput.productLineProperties.Add(new ProductLine("#FamilyInstances", "" + familyInstances));
                //fooInput.productLineProperties.Add(new ProductLine("#NOLFamilyInstances", "" + nolFamilyInstances));
                if (generateIfc)
                {
                    doRequest(() =>
                    {
                        IFCExportOptions options = null;
                        Transaction trans = new Transaction(doc);
                        trans.Start("Export IFC");
                        doc.Export(NOLClient + "cache", @"ifcexport.ifc", options);
                        trans.RollBack();
                    });
                }
                if (exportIfc)
                {
                    fooInput.ifcContent = System.IO.File.ReadAllText(NOLClient + @"cache\ifcexport.ifc");
                }
                if (exportProprietary && lastWrittenFamilyFileName != null)
                {
                    byte[] contents = System.IO.File.ReadAllBytes(lastWrittenFamilyFileName);
                    fooInput.proprietaryContent = System.Convert.ToBase64String(contents);
                    fooInput.productLineName = System.IO.Path.GetFileName(lastWrittenFamilyFileName);
                }
                string result = getSerializer().Serialize(fooInput);
                System.IO.File.WriteAllText(NOLClient + @"debug\getCurrentProperties.json", result);
                return result;
            }
            catch (Exception ex)
            {
                String message = ex.ToString();
                System.IO.File.WriteAllText(NOLClient + @"debug\ErrorMessage_getCurrentProperties.txt", message);
                throw ex;
            }
        }

        public static string GetId(Guid guid)
        {
            return IfcGuid.IfcGuid.ToIfcGuid(guid);
        }

        private string getIfcGuid(Element elem)
        {
            Parameter ifcGUID = elem.get_Parameter("IfcGUID");
            if (ifcGUID != null)
            {
                return GetParameterInformation(ifcGUID, doc);
            }
            else
            {
                return GetId(ExportUtils.GetExportId(doc, elem.Id));
            }
        }
        
        public void selectAll()
        {
            associateContext(true);
            List<Element> invalElems = new List<Element>();
            IniFile configIni = new IniFile(NOLClient + "config.ini");

            foreach (Element elem in elems)
            {
                
                HashSet<string> intersectedProps = null;
                Element luckyOne = null;
                List<string> propNames = null;
                Dictionary<string, ProductLine> backupValues = new Dictionary<string, ProductLine>();

                ObjectLibraryResponse foo = getBackup(elem);
                if (foo != null)
                {
                    propNames = new List<string>();
                    foreach (ProductLine line in foo.productLineProperties)
                    {
                        propNames.Add(line.name);
                        backupValues.Add(line.name, line);
                    }
                    if (intersectedProps == null)
                    {
                        intersectedProps = new HashSet<string>(propNames);
                    }
                    else
                    {
                        intersectedProps.IntersectWith(propNames);
                    }
                    luckyOne = elem;
                }

                if (luckyOne!=null) 
                {
                    CustomClass myProperties = new CustomClass();
                    loadProperties(luckyOne, propNames, intersectedProps, backupValues, myProperties);
                    validateLOD(myProperties, configIni, true);
                    
                    foreach (CustomProperty prop in myProperties)
                    {
                        if (prop.ErrorText != null)
                        {
                            invalElems.Add(elem);
                        }
                    }
                }
            }
            int totalNOLElements = elems.Count;
            if (invalElems.Count > 0)
            {
                elems = invalElems; 
            }
            
            uiApplication.ActiveUIDocument.Selection.SetElementIds(elems.Select(elem => elem.Id).ToList());
            
            uiApplication.ActiveUIDocument.RefreshActiveView();
            if (invalElems.Count > 0)
            {
                TaskDialog.Show("Selection of NOL objects", "Selected " + uiApplication.ActiveUIDocument.Selection.GetElementIds().Count + " errorneously specified NOL objects (from totally " + totalElements + " objects and from totally " + totalNOLElements + " NOL objects)");
                return;
            }
            TaskDialog.Show("Selection of NOL objects", "Selected " + uiApplication.ActiveUIDocument.Selection.GetElementIds().Count + " NOL objects (from totally " + totalElements + " objects)");
        }

        private void associateContext(bool fromAllObjects)
        {
            doc = uiApplication.ActiveUIDocument==null ? null : uiApplication.ActiveUIDocument.Document;
            targetCat = null;
            elems = new List<Element>();
            guidMap = new Dictionary<string, Element>();
            totalElements = 0;
            familyInstances = 0;
            nolFamilyInstances = 0;
            if (doc == null) return;
            if (doc.IsFamilyDocument)
            {
                targetCat = doc.OwnerFamily.FamilyCategory;
            }
            else
            if (fromAllObjects)
            {
                FilteredElementCollector collector = new FilteredElementCollector(doc);
                ElementClassFilter filterFamilyInstances = new ElementClassFilter(typeof(FamilyInstance));
                ElementClassFilter filterSystemObjects = new ElementClassFilter(typeof(HostObject));
                LogicalOrFilter filter = new LogicalOrFilter(filterFamilyInstances, filterSystemObjects);
                collector.WherePasses(filter);
                FilteredElementIterator elemItr = collector.GetElementIterator();
                elemItr.Reset();
                while (elemItr.MoveNext())
                {
                    Element el = elemItr.Current;
                    if (el is FamilyInstance) 
                    {
                        familyInstances++;
                    }
                    Parameter para = el.get_Parameter("NOL.Path");
                    if (para != null && GetParameterInformation(para, doc) != "")
                    {
                        elems.Add(el);
                        if (el is FamilyInstance)
                        {
                            nolFamilyInstances++;
                        }
                    }
                    guidMap.Add(getIfcGuid(el), el);
                    totalElements++;
                }
            }
            else
            {
                //Pick a group
                Selection sel = uiApplication.ActiveUIDocument.Selection;
                foreach (ElementId elId in sel.GetElementIds())
                {
                    Element el = doc.GetElement(elId);
                    if (el != null && (targetCat == null || targetCat.Name == el.Category.Name))
                    {
                        elems.Add(el);
                        targetCat = el.Category;
                    }
                    totalElements++;
                }
            }
        }

        public bool passed(int seconds)
        {
            long now = DateTime.Now.Ticks;
            long passed = ((now - lastMeasure) / 10000) / 1000;
            return (passed > seconds);
        }

        public void measure(String action)
        {
            long now = DateTime.Now.Ticks;
            long passed = ((now - lastMeasure) / 10000) / 1000;
            if (passed > 1)
                System.IO.File.AppendAllText(NOLClient + @"debug\performance.log", action + passed + " seconds\n");
            lastMeasure = now;
        }

        public void wakeUp()
        {
            measure("Idled away ");
        }


        public void importPSets(String json)
        {
            doRequest(() =>
            {


                measure("Time passed by since last operation: ");
                System.IO.File.WriteAllText(NOLClient + @"debug\importPSets.json", json);
                Transaction trans = null;
                try
                {
                    associateContext(false);
                    measure("Selecting Revit model elements took ");

                    if (doc != null)
                    {
                        try
                        {
                            trans = new Transaction(doc);
                            trans.Start("Test");
                            trans.RollBack();
                            trans = null;
                        }
                        catch (Exception ex)
                        {
                            TaskDialog.Show("Error", "Cannot import NOL content while being in another active transaction (e.g. the insert mode)! Error message:\n" + ex.Message);
                            return;
                        }
                    }

                    json = json.Replace("@xsi.type", "__type");

                    ObjectLibraryResponse foo = getDeserializer().Deserialize<ObjectLibraryResponse>(json);
                    System.IO.File.AppendAllText(NOLClient + @"debug\performance.log", "+++++++ Processing " + foo.productLineName + "\n");
                    measure("Deserialization of RaaS message took ");

                    if ("CREATELIB" == foo.accept && !doc.IsFamilyDocument || "COMPILE" == foo.accept)
                    {
                        silentMode = true;
                        String symbolName = null;
                        String fileName = null;
                        if (buildFamily(foo, null, out fileName, out symbolName) && "CREATELIB" == foo.accept)
                        {
                            trans = new Transaction(doc);
                            trans.Start("Lab");

                            FamilySymbol symbol = FindSymbol(symbolName, doc);

                            if (symbol == null)
                            {
                                Family family = null;

                                if (!doc.LoadFamily(fileName, out family))
                                {
                                    throw new Exception("Unable to load " + fileName);
                                }
                                measure("Loading family into project took ");
                            }

                            trans.Commit();
                            measure("Commit loading family took ");
                        }
                        silentMode = false;
                    }
                    else if (doc.IsFamilyDocument)
                    {
                        if (foo.representationItem.Count == 0)
                        {
                            TaskDialog.Show("National Object Library", "No geometry data available!");
                            return;
                        }

                        m_symbolName = "family";
                        buildGeom(foo, doc);

                        createFamilyParameters(doc, foo);

                        if (totalPrimitives == 0)
                        {
                            TaskDialog.Show("National Object Library", "Could not interpret geometry data!");
                        }
                        return;
                    }
                    else if ("ONLYVALUES" == foo.accept)
                    {
                        trans = new Transaction(doc);
                        trans.Start("Import properties");
                        bool needGUIDMap = false;
                        foreach (ProductLine line in foo.productLineProperties)
                        {
                            if (line.name.Equals("#GUID"))
                            {
                                needGUIDMap = true;
                                break;
                            }
                        }
                        if (needGUIDMap)
                        {
                            associateContext(true);
                            measure("Selecting all Revit model elements took ");
                            createProjectParameters(doc, foo, null, false);
                        }
                        else
                            foreach (Element elem in elems)
                            {
                                createProjectParameters(doc, foo, elem, false);
                            }
                        trans.Commit();
                        measure("Committing project parameters took ");
                    }
                    else
                    {
                        trans = new Transaction(doc);
                        trans.Start("Import properties");

                        foreach (Element elem in elems)
                        {
                            FamilyInstance familyInstance = elem as FamilyInstance;
                            if (familyInstance != null && importIni.IniReadValue("FORBIDDENFAMILY", targetCat.Name) != "true")
                            {
                                String symbolName = null;
                                String fileName = null; ;
                                if (buildFamily(foo, targetCat, out fileName, out symbolName))
                                {
                                    FamilySymbol symbol = FindSymbol(symbolName, doc);

                                    if (symbol == null)
                                    {
                                        Family family = null;

                                        if (!doc.LoadFamily(fileName, out family))
                                        {
                                            throw new Exception("Unable to load " + fileName);
                                        }
                                        measure("Loading family took ");
                                        IEnumerator<ElementId> symbolItor = family.GetFamilySymbolIds().GetEnumerator();
                                        while (symbolItor.MoveNext())
                                        {
                                            symbol = doc.GetElement(symbolItor.Current) as FamilySymbol;
                                        }
                                        if (symbol == null)
                                        {
                                            throw new Exception("Unable to find symbol in " + fileName);
                                        }
                                    }
                                    familyInstance.Symbol = symbol;
                                }
                            }
                            else createProjectParameters(doc, foo, elem, false);
                        }
                        trans.Commit();
                        measure("Committing took ");
                    }
                    wakeUp();
                    elems = null; // for updateObjectPropertiesPage() to update
                    updateObjectPropertiesPage();

                }

                //Catch other errors
                catch (Exception ex)
                {
                    String message = ex.ToString();
                    System.IO.File.WriteAllText(NOLClient + @"debug\ErrorMessage.txt", message);
                    if (trans != null)
                        trans.RollBack();
                    throw new Exception(message, ex);
                }

            });
        }

        private static void TriggerNolCommand()
        {
            IntPtr revitHandle = Autodesk.Windows.ComponentManager.ApplicationWindow;
            SetForegroundWindow(revitHandle);
            System.Windows.Forms.SendKeys.SendWait("{F2}");
        }

        private void createProjectParameters(Document doc, ObjectLibraryResponse foo, Element elem, Boolean onlyExisting)
        {
            wakeUp();
            int addedParams = 0;
            int notFound = 0;
            int receivedElements = 0;
            ObjectLibraryResponse backup = null;
            foreach (ProductLine line in foo.productLineProperties)
            {
                if (line.name.Equals("#GUID"))
                {
                    createBackup(doc, elem, ref backup);
                    String guid = line.value;
                    /*String a = elem.UniqueId.ToString();
                    long last_32_bits = long.Parse(a.Substring(28, 8), NumberStyles.AllowHexSpecifier);
                    long last_32_bits_NOL = long.Parse(line.value.Substring(28, 8), NumberStyles.AllowHexSpecifier);
                    long elementId = last_32_bits ^ last_32_bits_NOL;
                    elem = doc.get_Element(new ElementId((int)elementId));
                    if (elem != null)
                    {
                        Guid episodeId = new Guid(a.Substring(0, 36));
                        System.IO.File.WriteAllText(NOLClient + @"debug\GUID1.txt", " line.value=" + line.value + " a=" + a + " episodeId=" + episodeId + " last_32_bits_NOL=" + last_32_bits_NOL + " last_32_bits=" + last_32_bits + " elementId=" + elementId);
                    }
                    else*/
                    elem = guidMap[guid];
                    if (elem == null)
                    {
                        notFound++;
                    }
                    receivedElements++;
                    continue;
                }
                if (elem != null)
                {
                    if (line.name.Equals("Pset_Specification.Finish"))
                    {
                        Parameter builtInParameter = elem.get_Parameter(BuiltInParameter.DOOR_FINISH);
                        if (builtInParameter != null)
                        {
                            builtInParameter.Set(line.value);
                        }
                    }

                    if (line.name.EndsWith(".Description"))
                    {
                        Parameter builtInParameter = elem.get_Parameter(BuiltInParameter.ALL_MODEL_INSTANCE_COMMENTS);
                        if (builtInParameter != null)
                        {
                            builtInParameter.Set(line.value);
                        }
                    }

                    if (line.name.EndsWith(".ModelLabel"))
                    {
                        Parameter builtInParameter = elem.get_Parameter(BuiltInParameter.ALL_MODEL_MODEL);
                        if (builtInParameter != null)
                        {
                            builtInParameter.Set(line.value);
                        }
                    }
                    if (line.name.EndsWith(".Manufacturer"))
                    {
                        Parameter builtInParameter = elem.get_Parameter(BuiltInParameter.ALL_MODEL_MANUFACTURER);
                        if (builtInParameter != null)
                        {
                            builtInParameter.Set(line.value);
                        }
                    }
                }
                Parameter param = elem.get_Parameter(line.name);
                if (param==null && !onlyExisting) {
                    param = GetElementParameter(doc, m_revit, "Pset", line.name, ParameterType.Text, false,  BuiltInParameterGroup.PG_IFC, true, elem);
                }
                if (param != null)
                {
                    if (!param.IsReadOnly)
                    {
                        param.Set(line.value);
                    }
                    else
                    {
                        System.IO.File.AppendAllText(NOLClient + @"debug\createParameter.txt", "Parameter "+line.name+ " is read only!\n");
                    }
                    addedParams++;
                    if (backup == null)
                        backup = new ObjectLibraryResponse();
                    backup.productLineProperties.Add((ProductLine) line.Clone());                        
                }
            } 
            createBackup(doc, elem, ref backup);
            if (receivedElements >= 1)
            {
                String msg = (receivedElements - notFound) + " element(s) have been updated.\n";
                if (notFound >= 1)
                    msg += notFound + " GUIDs could not be found in the CAD model.\n";
                TaskDialog.Show("National Object Library", msg);
            }
            measure("Creating project properties took ");
        }

        private void createBackup(Document doc, Element elem, ref ObjectLibraryResponse backup)
        {

            if (backup != null && elem != null)
            {
                Parameter param = elem.get_Parameter("NOLBackup");
                if (param == null)
                {
                    param = GetElementParameter(doc, m_revit, "Pset", "NOLBackup", ParameterType.Text, false, BuiltInParameterGroup.PG_IFC, true, elem);
                }
                if (param != null)
                {
                    param.Set(getSerializer().Serialize(backup));
                }
                backup = null;
            }
        }

        private void createFamilyParameters(Document doc, ObjectLibraryResponse foo)
        {
            wakeUp();
            ObjectLibraryResponse backup = new ObjectLibraryResponse();
            Transaction trans = new Transaction(doc);
            trans.Start("Add family parameters");
            foreach (ProductLine line in foo.productLineProperties)
            {
                //ExternalDefinition definition = RawCreateProjectParameter(doc,m_revit, "Pset", line.name,  ParameterType.Text, true, cats1, BuiltInParameterGroup.PG_IFC, true);
                if (doc.FamilyManager.get_Parameter(line.name) == null)
                {
                    FamilyParameter param = doc.FamilyManager.AddParameter(line.name, BuiltInParameterGroup.PG_IFC, ParameterType.Text, true);
                    doc.FamilyManager.Set(param, line.value);
                    backup.productLineProperties.Add((ProductLine) line.Clone());
                }
            }
            if (doc.FamilyManager.get_Parameter("NOLBackup") == null)
            {
                FamilyParameter param = doc.FamilyManager.AddParameter("NOLBackup", BuiltInParameterGroup.PG_IFC, ParameterType.Text, true);
                doc.FamilyManager.Set(param, getSerializer().Serialize(backup));
            }
            measure("Creating family parameters took ");
            trans.Commit();
            measure("Committing family parameters took ");
        }

        //http://thebuildingcoder.typepad.com/blog/2010/04/collector-benchmark.html
        public FamilySymbol FindSymbol(string familyName, Document doc)
        {
            // get the family we want

            FilteredElementCollector fec
              = new FilteredElementCollector(doc);

            fec.OfClass(typeof(Family));

            IEnumerable<Family> families =
              from Family f in fec
              where f.Name == familyName
              select f;

            if (families.Count() == 0)
                return null;

            // get the symbols of that family

            FamilySymbolFilter fsf
              = new FamilySymbolFilter(
                  families.First<Family>().Id);

            fec = new FilteredElementCollector(doc);
            fec.WherePasses(fsf);

            // return first
            foreach (FamilySymbol fs in fec)
                return fs;

            return null;
        }

        // ==================================================================================
        //   helper function: find an element of the given type and the name.
        //   You can use this, for example, to find Reference or Level with the given name.
        // ==================================================================================
        Element findElement(Type targetType, string targetName, Document m_familyDocument)
        {
            // get the elements of the given type
            //
            //  ElementArray elems = new ElementArray();
            //  int n = m_familyDocument.get_Elements(targetType, elems);

            FilteredElementCollector collector = new FilteredElementCollector(m_familyDocument);
            ElementClassFilter filter = new ElementClassFilter(targetType);
            collector.WherePasses(filter);

            // parse the collection for the given name
            //
            foreach (Autodesk.Revit.DB.Element elem in collector)
            {
                //TaskDialog.Show("Revit", "Found " + elem.Name + " of type "+elem.Name.GetType().Name);
                if (elem.Name.Equals(targetName))  // we found it. return this.
                {
                    return elem;
                }
            }

            ElementId id = collector.FirstElementId();
            int objid = id.IntegerValue;

            // cannot find it.
            return null;
        }

        public List<XYZ> asPoints(List<IfcCartesianPoint> pts)
        {
            List<XYZ> points = new List<XYZ>();
            foreach (IfcCartesianPoint ifcCartesianPoint in pts)
            {
                XYZ xyz;
                if (ifcCartesianPoint.coordinates.Count == 3)
                    xyz = new XYZ(mmToFeet(ifcCartesianPoint.coordinates.ElementAt(0)), mmToFeet(ifcCartesianPoint.coordinates.ElementAt(1)), mmToFeet(ifcCartesianPoint.coordinates.ElementAt(2)));
                else
                    xyz = new XYZ(mmToFeet(ifcCartesianPoint.coordinates.ElementAt(0)), mmToFeet(ifcCartesianPoint.coordinates.ElementAt(1)), 0);
                points.Add(xyz);
            }
            XYZ first = points.ElementAt(0);
            XYZ last = points.ElementAt(points.Count - 1);
            if (!(first.X == last.X && first.Y == last.Y && first.Z == last.Z))
            {
                points.Add(first);
            }
            return points;
        }

        public void buildGeom(ObjectLibraryResponse foo, Document m_familyDocument)
        {

            wakeUp();
            Autodesk.Revit.Creation.FamilyItemFactory m_creationFamily = m_familyDocument.FamilyCreate;

            minZ = 10000;
            maxZ = -10000;
            minY = 10000;
            maxY = -10000;
            minX = 10000;
            maxX = -10000;
            offs = null;

            Transaction transaction = new Transaction(m_familyDocument, "Create geometry");
            transaction.Start();

            if (silentMode)
            {
                FailureHandlingOptions failOpt = transaction.GetFailureHandlingOptions();
                failOpt.SetFailuresPreprocessor(new RoomWarningSwallower());
                transaction.SetFailureHandlingOptions(failOpt);
            }

            sill = findElement(typeof(ReferencePlane), "Sill", m_familyDocument) as ReferencePlane;
            memberLeft = findElement(typeof(ReferencePlane), "Member Left", m_familyDocument) as ReferencePlane;
            memberRight = findElement(typeof(ReferencePlane), "Member Right", m_familyDocument) as ReferencePlane;
            pView = findElement(typeof(View), "Front", m_familyDocument) as View;
            centerLeftRight = findElement(typeof(ReferencePlane), "Center (Left/Right)", m_familyDocument) as ReferencePlane;
            viewLeft = findElement(typeof(View), "Left", m_familyDocument) as View;
            isStructuralFraming = memberLeft != null && memberRight != null && pView != null && viewLeft != null;
            Dimension dimWidth = findDim("Width", m_familyDocument);
            Dimension dimDepth = findDim("Depth", m_familyDocument);
            if (dimWidth == null) dimWidth = findDim("Truss Length", m_familyDocument);
            Dimension dimHeight = findDim("Height", m_familyDocument);
            if (dimHeight == null) dimHeight = findDim("Truss Height", m_familyDocument);
            levelUpperRefLevel = findElement(typeof(Level), "Upper Ref Level", m_familyDocument) as Level;
            levelLowerRefLevel = findElement(typeof(Level), "Lower Ref. Level", m_familyDocument) as Level;
            pViewLowerRefLevel = findElement(typeof(View), "Lower Ref. Level", m_familyDocument) as View;
            pViewRefLevel = findElement(typeof(View), "Ref. Level", m_familyDocument) as View; 
            planeLeft = findElement(typeof(ReferencePlane), "Left", m_familyDocument) as ReferencePlane;
            planeRight = findElement(typeof(ReferencePlane), "Right", m_familyDocument) as ReferencePlane;
            planeBack = findElement(typeof(ReferencePlane), "Back", m_familyDocument) as ReferencePlane;
            planeFront = findElement(typeof(ReferencePlane), "Front", m_familyDocument) as ReferencePlane;
            isColumn = planeLeft != null && planeRight != null && planeBack != null && planeFront != null && pViewLowerRefLevel != null && dimWidth != null && dimDepth != null && levelUpperRefLevel != null && levelLowerRefLevel!=null;

            /*
            FilteredElementCollector collector = new FilteredElementCollector(m_familyDocument);
            ElementClassFilter filter = new ElementClassFilter(typeof(Autodesk.Revit.DB.Extrusion));
            collector.WherePasses(filter);
            ElementId id = collector.FirstElementId();
            Autodesk.Revit.DB.Extrusion extrusion1 = (Autodesk.Revit.DB.Extrusion)m_familyDocument.get_Element(id);
            skplane = extrusion1.Sketch.SketchPlane;
             */

            preparsedTotalExtrusions = 0;
            preparsedTotalPolyloops = 0;
            processGeometryParts(foo, true, m_familyDocument, m_creationFamily);
            measure("Preparse geometry took ");
            preparsedTotalPolyloops = totalPolyloops;
            preparsedTotalExtrusions = totalExtrusions;
            if (isStructuralFraming)
            {
                //Add parametrics
                double tw = maxZ - minZ;
                double td = maxY - minY;
                addParamtricDimension(m_familyDocument, new XYZ(0, td, 0), "Left", "Center (Front/Back)", "yStart", "yEnd", "BeamWidth", out startPlaneY, out endPlaneY, XYZ.BasisZ);
                addParamtricDimension(m_familyDocument, new XYZ(0, 0, tw), "Left", "Center Elevation", "zStart", "zEnd", "BeamDepth", out startPlaneZ, out endPlaneZ, XYZ.BasisY);
            }
            if (isColumn && maxX != -10000)
            {
                m_familyDocument.FamilyManager.Set(dimWidth.FamilyLabel, maxX - minX);
                m_familyDocument.FamilyManager.Set(dimDepth.FamilyLabel, maxY - minY);

            }
            if (dimWidth != null && dimHeight != null && maxX != -10000)
            {
                m_familyDocument.FamilyManager.Set(dimWidth.FamilyLabel, maxX - minX);
                m_familyDocument.FamilyManager.Set(dimHeight.FamilyLabel, maxZ - minZ);
                //TaskDialog.Show("Revit", " mmX=" + (maxX-minX) + " maxX=" + maxX + " minX=" + minX + " offs=" + getOffs() + " dimWidth.Value=" + dimWidth.Value);
            }
            totalExtrusions = 0;
            totalPolyloops = 0;
            errorneousExtrusions = 0;
            errorneousPolyloops = 0;
            errorneousUnknownTypes = 0;
            oneUnknownType = null;
            materialMap = new Dictionary<string, Category>();
            processGeometryParts(foo, false, m_familyDocument, m_creationFamily);
            measure("Build geometry took ");
            int errorneousPrimitives = errorneousExtrusions + errorneousPolyloops + errorneousUnknownTypes;
            totalPrimitives = totalExtrusions + totalPolyloops + errorneousUnknownTypes;

            if (totalPrimitives == 0)
            {
                if (!silentMode) TaskDialog.Show("National Object Library", "Properties were downloaded, however no geometry was attached!");
            }

            transaction.Commit();
            measure("Commit geometry took ");

            if (errorneousPrimitives != 0)
            {
                String msg = errorneousPrimitives + " of totally " + totalPrimitives + " geometry parts of " + foo.productLineName + " were not imported:\n" + errorneousExtrusions + " of totally " + totalExtrusions + " extrusions not accepted by Revit\n" + errorneousPolyloops + " of totally " + totalPolyloops + " faces not accepted by Revit\n" + errorneousUnknownTypes + " not interpreted geometric types" + (oneUnknownType != null ? " e.g. " + oneUnknownType : "");
                if (!silentMode) TaskDialog.Show("National Object Library", msg);
                System.IO.File.WriteAllText(NOLClient + @"debug\buildGeometry.txt", msg);
            }
        }

        static public string getValueFor(string name, string category, IniFile configIni)
        {


            foreach (String substring in configIni.IniReadValue(category, null).Split('\0'))
            {
                String raw = substring.Replace("\"", "");
                if (raw.Length >= 1 && name.Contains(raw))
                {
                    return configIni.IniReadValue(category, substring);
                }
            }

            return null;

        }

        public void validateLOD()
        { 
            IniFile configIni = new IniFile(NOLClient + "config.ini");
            validateLOD(objectInspector.myProperties, configIni, false);
            objectInspector.PropertyGrid.Refresh();
            logProperties(objectInspector.myProperties);
        }


        static public void logProperties(CustomClass myProperties)
        {
            try
            {
                System.IO.File.WriteAllText(NOLClient + @"debug\ObjectInspector.csv", "Property,Current Value,Original Value,LodVisibility,LodReadOnly,LodMustChange,Error Text\n");
                foreach (CustomProperty prop in myProperties)
                {
                    System.IO.File.AppendAllText(NOLClient + @"debug\ObjectInspector.csv", withoutComma(prop.Category + "." + prop.Name) + "," + withoutComma("" + prop.Value) + "," + withoutComma("" + prop.OriginalValue) + "," + prop.LodVisibility + "," + prop.LodReadOnly + "," + prop.LodMustChange + "," + prop.ErrorText + "\n");
                }
            }
            catch (Exception) 
            {
            }
        }

        public void validateLOD(CustomClass myProperties, IniFile configIni, bool readOnly)
        {

            string lod = null;
            foreach (CustomProperty prop in myProperties)
            {
                if (prop.Name == "LOD")
                {
                    lod = ""+prop.Value;
                }
            }

            int iMaxGeometryLod = 0;
            String sGeometryLodFamily = null;
            foreach (CustomProperty prop in myProperties)
            {
                string name = prop.Category + "." + prop.Name;
                int iLod = 0;
                if (lod != null && System.Int32.TryParse(lod, out iLod))
                {
                    prop.Visible = prop.LodVisibility <= iLod;
                    prop.ReadOnly = prop.LodReadOnly != 0 && prop.LodReadOnly <= iLod;
                    prop.MustChange = prop.LodMustChange != 0 && prop.LodMustChange <= iLod;
                    if (prop.MustChange && prop.ReadOnly)
                    {
                        if (prop.LodMustChange > prop.LodReadOnly)
                        {
                            prop.ReadOnly = false;
                        }
                    }
                    int iGeometryLod = 0;
                    if ("Geometries".Equals(prop.Category) && prop.Name.StartsWith("Lod") && prop.Value!=null && !"".Equals(prop.Value) && 
                        System.Int32.TryParse(prop.Name.Substring(3), out iGeometryLod) && iLod >= iGeometryLod && iGeometryLod > iMaxGeometryLod)
                    {
                        sGeometryLodFamily = "" + prop.Value;
                    }
                }
                string sRegularExpression = getValueFor(name, "REGULAR_EXPRESSIONS", configIni);
                prop.RegularExpression = sRegularExpression;
            }

            if (!readOnly && sGeometryLodFamily != null)
            {
                try
                {

                    associateContext(false);

                    foreach (Element elem in elems)
                    {
                        if (elem is FamilyInstance)
                        {
                            FamilyInstance familyInstance = elem as FamilyInstance;

                            BuiltInCategory myCatEnum = (BuiltInCategory)familyInstance.Category.Id.IntegerValue;

                            Dictionary<string, List<FamilySymbol>> winFamilyTypes = FindFamilyTypes(doc, myCatEnum);

                            foreach (KeyValuePair<string, List<FamilySymbol>> entry in winFamilyTypes)
                            {
                                //TaskDialog.Show("Revit", entry.Key + ":" + sGeometryLodFamily);
                                if (sGeometryLodFamily.Equals(entry.Key))
                                {
                                    foreach (FamilySymbol item in entry.Value)
                                    if (familyInstance.Symbol != item)
                                    {
                                        Transaction trans = new Transaction(doc);
                                        trans.Start("Export IFC");
                                        familyInstance.Symbol = item;
                                        trans.Commit();
                                        break;
                                    }
                                }
                            }
                        }
                    }
                }
                catch (Exception) 
                {
                }
            }

        }

        public static Dictionary<string, List<FamilySymbol>> FindFamilyTypes(Document doc, BuiltInCategory cat)
        {
            return new FilteredElementCollector(doc)
                            .WherePasses(new ElementClassFilter(typeof(FamilySymbol)))
                            .WherePasses(new ElementCategoryFilter(cat))
                            .Cast<FamilySymbol>()
                            .GroupBy(e => e.Family.Name)
                            .ToDictionary(e => e.Key, e => e.ToList());
        }

        private void processGeometryParts(ObjectLibraryResponse foo, bool justMeasure, Document m_familyDocument, Autodesk.Revit.Creation.FamilyItemFactory m_creationFamily)
        {

            foreach (IfcRepresentationItem item in foo.representationItem)
            {
                if (!justMeasure)
                {
                    m_subCat = findMaterial(item, m_familyDocument);
                }
                else
                {
                    m_subCat = null;
                }
                IfcExtrudedAreaSolid sld = item as IfcExtrudedAreaSolid;
                IfcShellBasedSurfaceModel ifcShellBasedSurfaceModel = item as IfcShellBasedSurfaceModel;
                IfcFaceBasedSurfaceModel ifcFaceBasedSurfaceModel = item as IfcFaceBasedSurfaceModel;
                IfcManifoldSolidBrep ifcManifoldSolidBrep = item as IfcManifoldSolidBrep;
                if (ifcFaceBasedSurfaceModel != null)
                {
                    foreach (IfcConnectedFaceSet faceSet in ifcFaceBasedSurfaceModel.fbsmFaces)
                    {
                        processFace(m_familyDocument, m_creationFamily, faceSet, justMeasure);
                    }
                }
                else if (ifcShellBasedSurfaceModel != null)
                {
                    foreach (IfcOpenShell shell in ifcShellBasedSurfaceModel.sbsmBoundary)
                    {
                        processFace(m_familyDocument, m_creationFamily, shell, justMeasure);
                    }
                }
                else if (ifcManifoldSolidBrep != null)
                {
                    IfcClosedShell shell = ifcManifoldSolidBrep.outer;
                    if (shell != null)
                    {
                        processFace(m_familyDocument, m_creationFamily, shell, justMeasure);
                    }
                }
                else if (sld != null)
                {
                    Transform flatten = getTransform(sld.position);

                    XYZ location = asXYZ(sld.position.location.coordinates);
                    XYZ normal = flatten.OfPoint(asXYZo(sld.extrudedDirection.directionRatios)).Normalize();


                    IfcArbitraryClosedProfileDef profile = sld.sweptArea as IfcArbitraryClosedProfileDef;
                    IfcRectangleProfileDef rectangleProfile = sld.sweptArea as IfcRectangleProfileDef;
                    if (profile != null)
                    {
                        IfcPolyline polyline = profile.outerCurve as IfcPolyline;
                        if (polyline != null && polyline.points.Count >= 1)
                        {
                            List<XYZ> points = OfPoints(flatten, asPoints(polyline.points));
                            List<List<XYZ>> voids = new List<List<XYZ>>();
                            IfcArbitraryProfileDefWithVoids profileWithVoids = sld.sweptArea as IfcArbitraryProfileDefWithVoids;
                            if (profileWithVoids != null)
                            {
                                foreach (IfcPolyline innerCurve in profileWithVoids.innerCurves)
                                {
                                    voids.Add(OfPoints(flatten, asPoints(innerCurve.points)));
                                }
                            }
                            try
                            {
                                //TaskDialog.Show("Revit", "n=" + n + " depth=" + mmToFeet(sld.depth) + " depth ori=" + (sld.depth));
                                //printPolyline(n, points, polyline.points);
                                CreateExtrusion(normal, location, points, voids, sld.depth, m_creationFamily, m_familyDocument, justMeasure);
                            }
                            catch (Exception ex)
                            {
                                String message = ex.ToString();
                                System.IO.File.WriteAllText(NOLClient+@"debug\ErrorMessageExtrusion.txt", message);
                                errorneousExtrusions++;
                                try
                                {
                                    //try to use normal calculated from the plane containing some points
                                    CreateExtrusion(getNormalFromPolyline(points), location, points, voids, sld.depth, m_creationFamily, m_familyDocument, justMeasure);
                                    if (!silentMode) TaskDialog.Show("National Object Library", "SUCCESS at " + totalExtrusions + " is \n" + getNormalFromPolyline(points) + " compared to \n" + normal);
                                }
                                catch (Exception)
                                {
                                    if (!silentMode) TaskDialog.Show("National Object Library", "FAILURE at " + totalExtrusions + " is \n" + getNormalFromPolyline(points) + " compared to \n" + normal);
                                    if (!silentMode) printPolyline(totalExtrusions, points, polyline.points);
                                }

                            }
                            totalExtrusions++;
                        }
                        else if (!silentMode) TaskDialog.Show("National Object Library", "got no polyline but a " + profile.outerCurve);
                    }
                    else
                        if (rectangleProfile != null)
                        {
                            List<XYZ> points = new List<XYZ>();
                            double x = mmToFeet(rectangleProfile.XDim / 2);
                            double y = mmToFeet(rectangleProfile.YDim / 2);
                            points.Add(new XYZ(x, y, 0));
                            points.Add(new XYZ(-x, y, 0));
                            points.Add(new XYZ(-x, -y, 0));
                            points.Add(new XYZ(x, -y, 0));
                            points.Add(new XYZ(x, y, 0));
                            points = OfPoints(flatten, points);
                            try
                            {
                                CreateExtrusion(normal, location, points, new List<List<XYZ>>(), sld.depth, m_creationFamily, m_familyDocument, justMeasure);
                            }
                            catch (Exception ex)
                            {
                                String message = ex.ToString();
                                System.IO.File.WriteAllText(NOLClient+@"debug\ErrorMessageRectangleExtrusion.txt", message);
                                errorneousExtrusions++;
                            }
                            totalExtrusions++;
                        }
                        else if (!silentMode) TaskDialog.Show("National Object Library", "got no profile but got " + profile);
                }
                else
                {
                    errorneousUnknownTypes++;
                    oneUnknownType = item.GetType().Name;
                }
            }
        }
        
        private Color toHexColor(IfcColourRgb col, double factor)
        {
            return new Color(Convert.ToByte(col.red * 0xFF * factor), Convert.ToByte(col.green * 0xFF * factor), Convert.ToByte(col.blue * 0xFF * factor));
        }


        private Category findMaterial(IfcRepresentationItem item, Document m_familyDocument)
        {
            IfcStyledItem styledByItem = item.styledByItem;
            if (styledByItem != null)
            {
                foreach (IfcPresentationStyleAssignment _as in styledByItem.styles)
                {
                    foreach (IfcPresentationStyleSelect sel in _as.styles)
                    {
                        IfcSurfaceStyle surfaceStyle = sel.ifcSurfaceStylevalue;
                        if (surfaceStyle != null)
                        {
                            String name = surfaceStyle.name;

                            if (materialMap.ContainsKey(name))
                            {
                                return materialMap[name];
                            }

                            String suffix = " for " + m_symbolName;

                            // create a new subcategory  
                            Category cat = m_familyDocument.OwnerFamily.FamilyCategory;
                            Category subCat = m_familyDocument.Settings.Categories.NewSubcategory(cat, "MaterialCategory_" + name + suffix);

                            // create a new material and assign it to the subcategory  
                            ElementId materialId = Material.Create(m_familyDocument, name + " Material" + suffix);


                            subCat.Material = m_familyDocument.GetElement(materialId) as Material;

                            // assign the subcategory to the element  
                            materialMap.Add(name, subCat);

                            foreach (IfcSurfaceStyleElementSelect ifcSurfaceStyleElementSelect in surfaceStyle.styles)
                            {
                                if (ifcSurfaceStyleElementSelect is IfcSurfaceStyleRendering)
                                {
                                    IfcSurfaceStyleRendering ifcSurfaceStyleRendering = (IfcSurfaceStyleRendering)ifcSurfaceStyleElementSelect;
                                    IfcColourRgb col = ifcSurfaceStyleRendering.surfaceColour;
                                    if (col != null)
                                    {
                                        subCat.Material.Color = toHexColor(col, 1);
                                        Double tranparency = ifcSurfaceStyleRendering.transparency;
                                        if (tranparency != 0)
                                        {
                                            subCat.Material.Transparency = Convert.ToByte(tranparency*100);
                                        }
                                    }

                                }
                            }

                            return subCat;


                        }
                    }
                }
            }
            return null;
        }

        private Transform getTransform(IfcAxis2Placement3D placement)
        {
            XYZ r = XYZ.BasisX;
            if (placement.refDirection != null) r = asXYZo(placement.refDirection.directionRatios);
            XYZ a = XYZ.BasisZ;
            if (placement.axis != null) a = asXYZo(placement.axis.directionRatios);
            XYZ t = new XYZ(a.Y * r.Z - a.Z * r.Y, a.Z * r.X - a.X * r.Z, a.X * r.Y - a.Y * r.X);
            if (isStructuralFraming) { XYZ h = r; r = a; a = h; }
            if (isStructuralFraming) { XYZ h = t; t = r; r  = h; }
            Transform flatten = Transform.Identity;
            flatten.set_Basis(0, r);
            flatten.set_Basis(1, t);
            flatten.set_Basis(2, a);
            return flatten;
        }

        private XYZ getOffs()
        {
            if (offs == null)
            {
                offs = new XYZ(-minX - ((maxX - minX) / 2), -minY - ((maxY - minY) / 2), isStructuralFraming ? -minZ - ((maxZ - minZ) / 2) : ((sill != null ? sill.GetPlane().Origin.Z : 0) - minZ));
            }
            return offs;
        }

        private void processFace(Document m_familyDocument, Autodesk.Revit.Creation.FamilyItemFactory m_creationFamily, IfcConnectedFaceSet faceSet, bool justMeasure)
        {
            foreach (IfcFace face in faceSet.cfsFaces)
            {
                //TaskDialog.Show("National Object Library", "face.bounds " + face.bounds.Count);
                foreach (IfcFaceBound ifcFaceBound in face.bounds)
                {
                    IfcPolyLoop polyline = ifcFaceBound.bound as IfcPolyLoop;
                    if (polyline != null && polyline.polygon.Count >= 2)
                    {
                        List<XYZ> points = asPoints(polyline.polygon);

                        if (passed(60))
                        {
                            measure("Build geometry: intermediate proccessed " + totalPolyloops + "(from those are " + errorneousPolyloops + " errorneous, see ErrorMessageFace.txt) of totally " + preparsedTotalPolyloops + " faces; time passed: ");
                        }

                        if (justMeasure)
                        {
                            foreach (XYZ point in points)
                            {
                                doMinMax(point);
                            }
                        }
                        else
                        {
                            try
                            {
                                points = offset(points);
                                CreateFace(XYZ.BasisZ, points.ElementAt(0), points, m_creationFamily, m_familyDocument);
                            }
                            catch (Exception ex)
                            {
                                String message = ex.ToString();
                                System.IO.File.WriteAllText(NOLClient+@"debug\ErrorMessageFace.txt", message);
                                //List<IfcCartesianPoint> pts = polyline.polygon;
                                //printPolyline(totalPolyloops, points, pts);
                                errorneousPolyloops++;
                            }
                        }
                        totalPolyloops++;
                    }
                }
            }
        }

        private void doMinMax(XYZ point)
        {

            minZ = Math.Min(minZ, point.Z);
            maxZ = Math.Max(maxZ, point.Z);
            minY = Math.Min(minY, point.Y);
            maxY = Math.Max(maxY, point.Y);
            minX = Math.Min(minX, point.X);
            maxX = Math.Max(maxX, point.X);
        }

        private Dimension findDim(String name, Document m_familyDocument)
        {

            // Find all Wall instances in the document by using category filter
            ElementCategoryFilter filter = new ElementCategoryFilter(BuiltInCategory.OST_Dimensions);
            // Apply the filter to the elements in the active document,
            // Use shortcut WhereElementIsNotElementType() to find wall instances only
            FilteredElementCollector collector = new FilteredElementCollector(m_familyDocument);
            IList<Element> walls = collector.WherePasses(filter).WhereElementIsNotElementType().ToElements();
            foreach (Element e in walls)
            {
                Dimension dimension = e as Dimension;
                try
                {
                    if (dimension.FamilyLabel.Definition.Name == name)
                        return dimension;
                }
                catch (Exception)
                {
                }
            }
            return null;
        }

        private static void printPolyline(int n, List<XYZ> points, List<IfcCartesianPoint> pts)
        {
            String s = "";
            foreach (IfcCartesianPoint p in pts)
                s += " \n x=" + p.coordinates.ElementAt(0) + " y=" + p.coordinates.ElementAt(1) + (p.coordinates.Count == 3 ? " z=" + p.coordinates.ElementAt(2) : "");
            s += " \n points...";
            foreach (XYZ p in points)
                s += " \n x=" + p.X + " y=" + p.Y + " z=" + p.Z;
            TaskDialog.Show("National Object Library", "count " + pts.Count + " n=" + n + " \n normal= " + getNormalFromPolyline(points) + " \n original points=" + s);

        }

        private System.Collections.Generic.List<XYZ> OfPoints(Transform transform, System.Collections.Generic.List<XYZ> list)
        {
            List<XYZ> result = new List<XYZ>();
            foreach (XYZ point in list)
                result.Add(transform.OfPoint(point));
            return result;
        }

        public bool buildFamily(ObjectLibraryResponse foo, Category targetCat, out String fileName, out String symbolName)
        {
            wakeUp();
            String dir = NOLClient+@"cache\RevitFamilies\";
            String name = lastWrittenFamilyFileName = null;
            if (foo.proprietaryContent == null)
            if (targetCat != null)
            {
                name = targetCat.Name;
            }
            else 
            {
                name = getValueFor(foo.productLineName, "NAMEMATCHING", importIni);
                if (name == null)
                {
                    String msg = "Found no category for " + foo.productLineName;
                    System.IO.File.WriteAllText(NOLClient+@"debug\buildFamily.txt", msg);
                    TaskDialog.Show("National Object Library", msg);
                    symbolName = "";
                    fileName = "";
                    return false;
                }
            }
            System.IO.File.WriteAllText(NOLClient+@"debug\buildFamily.txt", "Found category "+name+" for " + foo.productLineName);
            symbolName = name != null ? name + "_" + foo.productLineName : foo.productLineName;
            symbolName = symbolName.Replace('\\', '_').Replace('/', '_').Replace(':', '_').Replace('*', '_').Replace('?', '_').Replace('"', '_').Replace('<', '_').Replace('>', '_').Replace('|', '_');
            fileName = dir + symbolName + ".rfa";
            if (File.Exists(fileName))
            {
                lastWrittenFamilyFileName = fileName;
                return true;
            }

            Directory.CreateDirectory(dir);

            Document m_familyDocument;
            if (foo.proprietaryContent != null)
            {
                byte[] content = System.Convert.FromBase64String(foo.proprietaryContent);
                if (foo.productLineProperties.Count == 0 || true/*not at all change a prepared family, so that the UI is not messed up!*/)
                {
                    System.IO.File.WriteAllBytes(fileName, content);
                    return true;
                }
                System.IO.File.WriteAllBytes(fileName + "ori.rfa", content);
                m_familyDocument = m_revit.OpenDocumentFile(fileName + "ori.rfa");
            }
            else
            {
                String path = NOLClient+@"revit\templates\" + name + ".rft";
                if (!File.Exists(path))
                {
                    String msg = "Found category " + name + " for " + foo.productLineName + " but found no file " + path;
                    System.IO.File.WriteAllText(NOLClient + @"debug\buildFamily.txt", msg);
                    TaskDialog.Show("National Object Library", msg);
                    return false;
                }
                m_familyDocument = m_revit.NewFamilyDocument(path);
            }

            measure("Prepare family took ");
            if (foo.proprietaryContent == null)
            {
                m_symbolName = symbolName;
                buildGeom(foo, m_familyDocument);
            }

            // http://thebuildingcoder.typepad.com/blog/parameters/
            if (m_familyDocument.FamilyManager.Types.Size == 0)
            {
                Transaction transaction = new Transaction(m_familyDocument, "Create type");
                transaction.Start();
                m_familyDocument.FamilyManager.NewType(symbolName);
                transaction.Commit();
                measure("Commit family took ");
            }
            createFamilyParameters(m_familyDocument, foo);

            m_familyDocument.SaveAs(fileName);
            lastWrittenFamilyFileName = fileName;
            m_familyDocument.Close(false);
            if (foo.proprietaryContent != null)
            {
                System.IO.File.Delete(fileName + "ori.rfa");
            }
            System.IO.File.WriteAllText(NOLClient + @"debug\buildFamily.txt", "Found category " + name + " for " + foo.productLineName + " to file " + fileName);
            measure("Saving family took ");
            return true;
        }


        XYZ asXYZ(List<Double> coords)
        {
            return new XYZ(mmToFeet(coords.ElementAt(0)), mmToFeet(coords.ElementAt(1)), mmToFeet(coords.ElementAt(2)));
        }
        XYZ asXYZ(XYZ coords)
        {
            return new XYZ(mmToFeet(coords.X), mmToFeet(coords.Y), mmToFeet(coords.Z));
        }

        XYZ asXYZo(List<Double> coords)
        {
            return new XYZ(coords.ElementAt(0), coords.ElementAt(1), coords.ElementAt(2));
        }

        // ===============================================
        //   helper function: convert millimeter to feet
        // ===============================================
        double mmToFeet(double mmVal)
        {
            return mmVal / 304.8;
            //return mmVal;
        }

        /// <summary>
        /// Create sketch plane for generic model profile
        /// </summary>
        /// <param name="normal">plane normal</param>
        /// <param name="origin">origin point</param>
        /// <returns></returns>
        internal SketchPlane CreateSketchPlane(Autodesk.Revit.DB.XYZ normal, Autodesk.Revit.DB.XYZ origin, Autodesk.Revit.Creation.FamilyItemFactory fif, Document m_familyDocument)
        {
            Plane geometryPlane = null;
            if (centerLeftRight != null && centerLeftRight.GetPlane().Normal.IsAlmostEqualTo(normal) && centerLeftRight.GetPlane().Origin.IsAlmostEqualTo( origin))
            {
                geometryPlane = centerLeftRight.GetPlane();
            }
            else
            {
                // First create a Geometry.Plane which need in NewSketchPlane() method
                geometryPlane = Plane.CreateByNormalAndOrigin(normal, origin);
            }
            if (null == geometryPlane)  // assert the creation is successful
            {
                throw new Exception("Create the geometry plane failed.");
            }
            // Then create a sketch plane using the Geometry.Plane
            SketchPlane plane = SketchPlane.Create(m_familyDocument, geometryPlane);
            // throw exception if creation failed
            if (null == plane)
            {
                throw new Exception("Create the sketch plane failed.");
            }
            return plane;
        }

        private CurveArray asCurve(List<XYZ> points)
        {
            CurveArray curveArray1 = new CurveArray();
            XYZ prevPoint = null;
            foreach (XYZ point in points)
            {
                if (prevPoint != null)
                {
                    double dist = point.DistanceTo(prevPoint);
                    if (dist >= m_revit.ShortCurveTolerance)
                    {
                        Line line1 = Line.CreateBound(prevPoint, point);
                        curveArray1.Append(line1);
                    } else
                    {
                        System.IO.File.WriteAllText(NOLClient + @"debug\ErrorShortCurveTolerance.txt", "Short curve tolerance is "+ m_revit.ShortCurveTolerance+" but point distance is only "+ dist);
                        continue;
                    }
                }
                prevPoint = point;
            }
            return curveArray1;
        }

        private ReferenceArray asModelCurve(CurveArray curveArray, SketchPlane sketchPlane, Autodesk.Revit.Creation.FamilyItemFactory fif)
        {
            ReferenceArray ref_ar = new ReferenceArray();
            foreach (Curve curve in curveArray)
            {
                ModelCurve modelcurve = fif.NewModelCurve(curve, sketchPlane);
                ref_ar.Append(modelcurve.GeometryCurve.Reference);
            }
            return ref_ar;
        }

        private List<XYZ> offset(List<XYZ> points)
        {
            List<XYZ> result = new List<XYZ>();
            foreach (XYZ point in points)
            {
                result.Add(new XYZ(point.X + getOffs().X, point.Y + getOffs().Y, point.Z + getOffs().Z));
            }
            return result;
        }

        private void CreateExtrusion(XYZ normal, XYZ origin, List<XYZ> points, List<List<XYZ>> voids, double height, Autodesk.Revit.Creation.FamilyItemFactory fif, Document m_familyDocument, bool justMeasure)
        {

            if (passed(60))
            {
                measure("Build geometry: intermediate proccessed " + totalExtrusions + "(from those are " + errorneousExtrusions + " errorneous, see ErrorMessageExtrusion.txt) of totally " + preparsedTotalExtrusions + " extrusions; time passed: ");
            }

            height = mmToFeet(height);
            if (height < SMALLEST_EXTRUSION_DEPTH)
                height = SMALLEST_EXTRUSION_DEPTH;
            if (justMeasure)
            {
                foreach (XYZ p in points)
                {
                    doMinMax(p + origin);
                    doMinMax((p + origin) + (normal * height));
                }
                return;
            }

            XYZ or = XYZ.Zero;
            CurveArrArray curveArrArray = new CurveArrArray();

            SketchPlane sketchPlane = CreateSketchPlane(normal, or, fif, m_familyDocument);

            curveArrArray.Append(asCurve(points));
            foreach (List<XYZ> v in voids)
            {
                curveArrArray.Append(asCurve(v));
            }

            // here create rectangular extrusion
            //if (skplane!=null) sketchPlane = skplane;
            Extrusion rectExtrusion = fif.NewExtrusion(true, curveArrArray, sketchPlane, height);
            if (m_subCat != null)
            {
                rectExtrusion.Subcategory = m_subCat;
            }
            /*Parameter parameter = rectExtrusion.get_Parameter("Work Plane");
            if (parameter != null)
            {
                parameter.Set(centerLeftRight.Id);
                TaskDialog.Show("parameter=", "paam+=" + parameter.AsString());
            }
            */
            // move to proper place
            ElementTransformUtils.MoveElement(m_familyDocument,rectExtrusion.Id, getOffs() + origin);

            if (isStructuralFraming)
            {
                alignFace(m_familyDocument, rectExtrusion, new XYZ(-1.0, 0.0, 0.0), memberLeft, pView);
                alignFace(m_familyDocument, rectExtrusion, new XYZ(1.0, 0.0, 0.0), memberRight, pView);
                alignFace(m_familyDocument, rectExtrusion, new XYZ(0.0, 1.0, 0.0), endPlaneY, viewLeft);
                alignFace(m_familyDocument, rectExtrusion, new XYZ(0.0, -1.0, 0.0), startPlaneY, viewLeft);
                alignFace(m_familyDocument, rectExtrusion, new XYZ(0.0, 0.0, 1.0), endPlaneZ, viewLeft);
                alignFace(m_familyDocument, rectExtrusion, new XYZ(0.0, 0.0, -1.0), startPlaneZ, viewLeft);
            }
            if (isColumn)
            {
                alignFace(m_familyDocument, rectExtrusion, new XYZ(-1.0, 0.0, 0.0), planeLeft, pViewLowerRefLevel);
                alignFace(m_familyDocument, rectExtrusion, new XYZ(1.0, 0.0, 0.0), planeRight, pViewLowerRefLevel);
                alignFace(m_familyDocument, rectExtrusion, new XYZ(0.0, -1.0, 0.0), planeFront, pViewLowerRefLevel);
                //alignFace(m_familyDocument, rectExtrusion, new XYZ(0.0, 1.0, 0.0), planeBack, pViewLowerRefLevel);
                alignFace(m_familyDocument, rectExtrusion, new XYZ(0.0, 0.0, 1.0), levelUpperRefLevel, viewLeft);
                alignFace(m_familyDocument, rectExtrusion, new XYZ(0.0, 0.0, -1.0), levelLowerRefLevel, viewLeft);
            }
        }

        private void alignFace(Document m_familyDocument, Extrusion rectExtrusion, XYZ normal, ReferencePlane refPlane, View pView)
        {
            PlanarFace face = findFace(rectExtrusion, normal);
            if (face != null)
            {
                refPlane.Location.Move(normal * (face.Origin - refPlane.GetPlane().Origin).DotProduct(normal));
                m_familyDocument.FamilyCreate.NewAlignment(pView, refPlane.GetReference(), face.Reference);
            }
        }

        private void alignFace(Document m_familyDocument, Extrusion rectExtrusion, XYZ normal, Level refPlane, View pView)
        {
            PlanarFace face = findFace(rectExtrusion, normal);
            if (face != null)
            {
                refPlane.Location.Move(normal * (face.Origin.DotProduct(normal) - refPlane.Elevation));
                m_familyDocument.FamilyCreate.NewAlignment(pView, refPlane.GetPlaneReference(), face.Reference);
            }
        }

        // =============================================================
        //   helper function: find a planar face with the given normal
        // =============================================================
        PlanarFace findFace(Extrusion pBox, XYZ normal)
        {
            // get the geometry object of the given element
            //
            Options op = new Options();
            op.ComputeReferences = true;
            var geomObjs = pBox.get_Geometry(op);

            // loop through the array and find a face with the given normal
            //
            foreach (GeometryObject geomObj in geomObjs)
            {
                if (geomObj is Solid)  // solid is what we are interested in.
                {
                    Solid pSolid = geomObj as Solid;
                    FaceArray faces = pSolid.Faces;
                    foreach (Face pFace in faces)
                    {
                        PlanarFace pPlanarFace = (PlanarFace)pFace;
                        if ((pPlanarFace != null) && pPlanarFace.FaceNormal.IsAlmostEqualTo(normal)) // we found the face
                        {
                            return pPlanarFace;
                        }
                    }
                }
                // will come back later as needed.
                //
                //else if (geomObj is Instance)
                //{
                //}
                //else if (geomObj is Curve)
                //{
                //}
                //else if (geomObj is Mesh)
                //{
                //}
            }

            // if we come here, we did not find any.
            return null;
        }

        void addParamtricDimension(Document m_familyDocument, XYZ td, string viewName, string refCenterName, string startPlaneName, string endPlaneName, string paramName, out ReferencePlane startPlane, out ReferencePlane endPlane, XYZ cutVec)
        {
            View pViewPlan = findElement(typeof(View), viewName, m_familyDocument) as View;

            ReferencePlane refCenter = findElement(typeof(ReferencePlane), refCenterName, m_familyDocument) as ReferencePlane;

            XYZ pBubbleEnd = refCenter.BubbleEnd + td / 2;
            XYZ pFreeEnd = refCenter.FreeEnd + td / 2;
            ReferencePlane refPlane = m_familyDocument.FamilyCreate.NewReferencePlane(pBubbleEnd, pFreeEnd, cutVec, pViewPlan);
            refPlane.Name = endPlaneName;
            endPlane = refPlane;

            XYZ pBubbleEnd2 = refCenter.BubbleEnd - td / 2;
            XYZ pFreeEnd2 = refCenter.FreeEnd - td / 2;
            ReferencePlane refPlane2 = m_familyDocument.FamilyCreate.NewReferencePlane(pBubbleEnd2, pFreeEnd2, cutVec, pViewPlan);
            refPlane2.Name = startPlaneName;
            startPlane = refPlane2;

            FamilyParameter paramBeamWidth = m_familyDocument.FamilyManager.AddParameter(paramName, BuiltInParameterGroup.PG_GEOMETRY, ParameterType.Length, false);
            m_familyDocument.FamilyManager.Set(paramBeamWidth, td.X + td.Y + td.Z);

            Line pLine = Line.CreateBound(refPlane2.FreeEnd, refPlane.FreeEnd);
            ReferenceArray pRefArray = new ReferenceArray();
            pRefArray.Append(refPlane2.GetReference());
            pRefArray.Append(refCenter.GetReference());
            pRefArray.Append(refPlane.GetReference());
            Dimension pDimTw = m_familyDocument.FamilyCreate.NewDimension(pViewPlan, pLine, pRefArray);
            pDimTw.AreSegmentsEqual = true;

            pLine = Line.CreateBound(refPlane2.BubbleEnd, refPlane.BubbleEnd);
            pRefArray = new ReferenceArray();
            pRefArray.Append(refPlane2.GetReference());
            pRefArray.Append(refPlane.GetReference());
            pDimTw = m_familyDocument.FamilyCreate.NewDimension(pViewPlan, pLine, pRefArray);
            FamilyParameter paramTw = m_familyDocument.FamilyManager.get_Parameter(paramName);
            pDimTw.FamilyLabel = paramTw;
            
        }

        private void CreateFace(XYZ normal, XYZ origin, List<XYZ> points, Autodesk.Revit.Creation.FamilyItemFactory fif, Document m_familyDocument)
        {
            if (polygonAsForm)
            {

                XYZ n = getNormalFromPolyline(points);
                SketchPlane sketchPlane = CreateSketchPlane(n, origin, fif, m_familyDocument);

                CurveArray curveArray = asCurve(points);
                ReferenceArray profile = asModelCurve(curveArray, sketchPlane, fif);

                Autodesk.Revit.DB.Form singleSurfaceForm = fif.NewFormByCap(true, profile);
                Autodesk.Revit.DB.Form form = fif.NewFormByThickenSingleSurface(true, singleSurfaceForm, n.Multiply(SMALLEST_EXTRUSION_DEPTH));
                return;
            }
            if (!polygonAsExtrusion)
            {

                FilteredElementCollector collector = new FilteredElementCollector(m_familyDocument);
                IList<Element> fillRegionTypes = collector.OfClass(typeof(FilledRegionType)).ToElements();

                foreach (FilledRegionType frt in fillRegionTypes)
                {

                    List<CurveLoop> profileloops = new List<CurveLoop>();
                    CurveLoop profileloop = new CurveLoop();
                    for (int i = 0; i < points.Count - 1; i++)
                    {
                        Line line = Line.CreateBound(points[i], points[i + 1]);
                        profileloop.Append(line);
                    }
                    profileloops.Add(profileloop);
                    FilledRegion filledRegion = FilledRegion.Create(m_familyDocument, frt.Id, pViewRefLevel.Id, profileloops);
                    return;
                }

            }

            {
                SketchPlane sketchPlane = CreateSketchPlane(getNormalFromPolyline(points), origin, fif, m_familyDocument);

                CurveArray curveArray = asCurve(points);


                CurveArrArray curveArrArray = new CurveArrArray();
                curveArrArray.Append(curveArray);
                Extrusion rectExtrusion = fif.NewExtrusion(true, curveArrArray, sketchPlane, SMALLEST_EXTRUSION_DEPTH);
                if (m_subCat != null)
                {
                    rectExtrusion.Subcategory = m_subCat;
                }
                //fif.NewModelCurveArray(curveArray, sketchPlane);
            }

        }

        static private XYZ getNormalFromPolyline(System.Collections.Generic.List<XYZ> points)
        {
            XYZ p1 = points.ElementAt(2) - points.ElementAt(1);
            XYZ p2 = points.ElementAt(0) - points.ElementAt(1);
            return p1.CrossProduct(p2).Normalize();
        }


        public ExternalDefinition RawCreateProjectParameter(Document doc, Application app, string defGroup, string name, ParameterType type, bool visible, BuiltInParameterGroup group, bool inst, Category cat)
        {
            ExternalDefinition def = null;
            string oriFile = app.SharedParametersFilename;

            if (oriFile != null && oriFile != "" && app.OpenSharedParameterFile()!=null)
            {
                DefinitionGroup definitionGroup = app.OpenSharedParameterFile().Groups.get_Item(defGroup);
                if (definitionGroup != null)
                {
                    Definition definition = definitionGroup.Definitions.get_Item(name);
                    if (definition is ExternalDefinition)
                    {
                        def = definition as ExternalDefinition;
                        System.IO.File.AppendAllText(NOLClient + @"debug\DefinitionGroup.txt", name + " is reused\n");
                    }
                }
            }

            string tempFile = System.IO.Path.GetTempFileName() + ".txt";
            using (System.IO.File.Create(tempFile)) { }
            app.SharedParametersFilename = tempFile;

            if (def==null)
            {
                var options = new ExternalDefinitionCreationOptions(name, type);
                options.Visible = visible;
                def = app.OpenSharedParameterFile().Groups.Create(defGroup).Definitions.Create(options) as ExternalDefinition;
            }
            app.SharedParametersFilename = oriFile;
            System.IO.File.Delete(tempFile);

            ElementBinding binding;
            BindingMap map = doc.ParameterBindings;

            Autodesk.Revit.DB.Binding existingBinding = map.get_Item(def);
            if (existingBinding is ElementBinding)
            {
                binding = existingBinding as ElementBinding;
                CategorySet cats = binding.Categories;
                cats.Insert(cat);
                if (inst) binding = app.Create.NewInstanceBinding(cats); else binding = app.Create.NewTypeBinding(cats);
                map.ReInsert(def, binding, group);
                System.IO.File.AppendAllText(NOLClient + @"debug\ProjectParameters.txt", name + " is recreated for category " + cat.Name + "\n");
            }
            else
            {
                CategorySet cats = m_revit.Create.NewCategorySet();
                cats.Insert(cat);
                if (inst) binding = app.Create.NewInstanceBinding(cats); else binding = app.Create.NewTypeBinding(cats);
                map.Insert(def, binding, group);
                System.IO.File.AppendAllText(NOLClient + @"debug\ProjectParameters.txt", name + " is created for category "+cat.Name+"\n");
            }

            return def;
        }
        
        public Parameter GetElementParameter(Document doc, Application app, string defGroup, string name, ParameterType type, bool visible, BuiltInParameterGroup group, bool inst, Element elem)
        {
            String realName = name;
            Parameter param = elem==null?null:elem.get_Parameter(realName);
            if (param == null)
            {
                Guid guid = RawCreateProjectParameter(doc, app, defGroup, realName, type, visible, group, inst, elem.Category).GUID;
                if (elem!=null)
                    param = elem.get_Parameter(guid);
            }
            return param;
        }


        String GetParameterInformation(Parameter para, Document document)
        {
            //string defName = para.Definition.Name + "(" + para.StorageType.ToString() + ")" + "(" + para.Definition.ParameterType.ToString() + ")" + "(" + para.Definition.ParameterGroup.ToString() + ")";
            // Use different method to get parameter data according to the storage type
            switch (para.StorageType)
            {
                case StorageType.Double:
                    //covert the number into Metric
                    return para.AsValueString();
                case StorageType.ElementId:
                    //find out the name of the elementElementId id = para.AsElementId();
                    ElementId id = para.AsElementId();
                    if (id.IntegerValue >= 0)
                    {
                        return document.GetElement(id).Name + "[" + id.IntegerValue.ToString() + "]";
                    }
                    else
                    {
                        return id.IntegerValue.ToString();
                    }
                case StorageType.Integer:
                    if (ParameterType.YesNo == para.Definition.ParameterType)
                    {
                        if (para.AsInteger() == 0)
                        {
                            return "False";
                        }
                        else
                        {
                            return "True";
                        }
                    }
                    else
                    {
                        return para.AsInteger().ToString();
                    }
                case StorageType.String:
                    return para.AsString();
            }
            return "Unexposed parameter.";
        }


    }

    /// <summary>
    /// as __type is missing ,we need to add this
    /// </summary>
    public class ManualResolver : SimpleTypeResolver
    {
        public ManualResolver() { }
        public override Type ResolveType(string id)
        {
            Type type = Type.GetType(id);
            if (type == null)
            {
                type = typeof(Object);
            //    throw new Exception("NOL: Cannot find geometric type " + id);
            }
            return type;
        }
    }
    
    public class NullPropertiesConverter : JavaScriptConverter
    {
        public override object Deserialize(IDictionary<string, object> dictionary, Type type, JavaScriptSerializer serializer)
        {
            throw new NotImplementedException();
        }

        public override IDictionary<string, object> Serialize(object obj, JavaScriptSerializer serializer)
        {
            var jsonExample = new Dictionary<string, object>();
            foreach (var prop in obj.GetType().GetProperties())
            {
                var value = prop.GetValue(obj, BindingFlags.Public, null, null, null);
                if (value != null && !(value is ICollection && ((ICollection)value).Count == 0))
                    jsonExample.Add(prop.Name, value);
            }

            return jsonExample;
        }

        public override IEnumerable<Type> SupportedTypes
        {
            get { return GetType().Assembly.GetTypes(); }
        }
    }

    public class RoomWarningSwallower : IFailuresPreprocessor
    {
        public FailureProcessingResult PreprocessFailures(
          FailuresAccessor a)
        {
            // inside event handler, get all warnings

            IList<FailureMessageAccessor> failures
              = a.GetFailureMessages();

            foreach (FailureMessageAccessor f in failures)
            {
                // check failure definition ids 
                // against ones to dismiss:

                FailureDefinitionId id
                  = f.GetFailureDefinitionId();

                //if (BuiltInFailures.RoomFailures.RoomNotEnclosed == id)
                {
                    a.DeleteWarning(f);
                }
            }
            return FailureProcessingResult.Continue;
        }
    }

    public class NOLProperty : CustomProperty
    {
        const string NOLClient = "C:\\bim\\NOLClient\\";

        public NOLProperty(string sName, object value, object oriValue, Type type, bool bVisible, string sCategory, string sDescription, TypeConverter sTypeConverter, NationalObjectLibraryClass myObjectPropertiesPage)
            : base(sName, value, oriValue, type, sCategory == "NOL", bVisible, sCategory, sDescription)
        {
            this.myObjectPropertiesPage = myObjectPropertiesPage;
            if (Name == "LOD")
            {
                this.sDescription = "Level of Detail";
                this.sTypeConverter = new ComboConverter(new string[] { "100", "200", "300", "400", "500" });
            }
        }

        public NationalObjectLibraryClass myObjectPropertiesPage;

        public override object Value
        {
            get
            {
                return objValue;
            }
            set
            {
                objValue = value;
                myObjectPropertiesPage.updateProperty(sCategory + "." + Name, "" + value);
            }
        }

    }
    /// <summary>
    /// Wrapper class for converting 
    /// IntPtr to IWin32Window.
    /// </summary>
    public class JtWindowHandle : System.Windows.Forms.IWin32Window
    {
        IntPtr _hwnd;

        public JtWindowHandle(IntPtr h)
        {
            //Debug.Assert(IntPtr.Zero != h, "expected non-null window handle");

            _hwnd = h;
        }

        public IntPtr Handle
        {
            get
            {
                return _hwnd;
            }
        }
    }

    public static class HelperUtils
    {

        public static Parameter get_Parameter(this Element elem, string name)
        {
            try
            {
                return elem.ParametersMap.get_Item(name);
            }
            catch (Exception)
            {
                return null;
            }
        }

    }


}


public class ProductLine : ICloneable
{
    public ProductLine()
    { 
    }
    public ProductLine(String aName, String aValue)
    {
        name = aName;
        value = aValue;
    }
    public object Clone()
    {
        var result = new ProductLine(name, value);
        result.lodVisibility = lodVisibility;
        result.lodReadOnly = lodReadOnly;
        result.lodMustChange = lodMustChange;
        return result;
    }
    public String name { get; set; }
    public String value { get; set; }
    public int lodVisibility { get; set; }
    public int lodReadOnly { get; set; }
    public int lodMustChange { get; set; }
}


public class IfcAxis2Placement3D : IfcPlacement
{
    public IfcDirection refDirection { get; set; }
    public IfcDirection axis { get; set; }
}
public class IfcAxis2Placement2D : IfcPlacement
{
    public IfcDirection refDirection { get; set; }
}
public class IfcPlacement
{
    public IfcCartesianPoint location { get; set; }
}


public class IfcExtrudedAreaSolid : IfcSweptAreaSolid
{
    public Double depth { get; set; }
    public IfcDirection extrudedDirection { get; set; }
    public IfcAxis2Placement3D position { get; set; }
}
public class IfcSweptAreaSolid : IfcRepresentationItem
{
    public IfcProfileDef sweptArea { get; set; }
}
public class IfcShellBasedSurfaceModel : IfcGeometricRepresentationItem
{
    public IfcShellBasedSurfaceModel()
    {
        sbsmBoundary = new List<IfcConnectedFaceSet>();
    }
    public List<IfcConnectedFaceSet> sbsmBoundary { get; set; }
}
public class IfcManifoldSolidBrep : IfcGeometricRepresentationItem
{
    public IfcClosedShell outer { get; set; }
}
public class IfcFacetedBrep : IfcManifoldSolidBrep
{
}
public class IfcGeometricRepresentationItem : IfcRepresentationItem
{
}
public class IfcRepresentationItem
{
    public IfcStyledItem styledByItem { get; set; }
}

public class IfcStyledItem
{
    public IfcStyledItem()
    {
        styles = new List<IfcPresentationStyleAssignment>();
    }
    public List<IfcPresentationStyleAssignment> styles { get; set; }
}
public class IfcPresentationStyleAssignment
{
    public IfcPresentationStyleAssignment()
    {
        styles = new List<IfcPresentationStyleSelect>();
    }
    public List<IfcPresentationStyleSelect> styles { get; set; }
}
public class IfcPresentationStyleSelect
{
    public IfcSurfaceStyle ifcSurfaceStylevalue { get; set; }
}
public class IfcSurfaceStyle
{
    public IfcSurfaceStyle()
    {
        styles = new List<IfcSurfaceStyleElementSelect>();
    }
    public String name { get; set; }
    public List<IfcSurfaceStyleElementSelect> styles { get; set; }
}
public class IfcSurfaceStyleElementSelect 
{
}
public class IfcSurfaceStyleRendering : IfcSurfaceStyleElementSelect
{
    public IfcColourRgb surfaceColour { get; set; }
    public Double transparency { get; set; }
    public IfcColourOrFactor diffuseColour { get; set; }
}
public class IfcColourOrFactor
{
    public IfcColourRgb ifcColourRgbvalue { get; set; }
    public Double ifcNormalisedRatioMeasurevalue { get; set; }
}
public class IfcColourRgb
{
    public double red { get; set; }
    public double green { get; set; }
    public double blue { get; set; }
}

public class IfcFaceBasedSurfaceModel : IfcGeometricRepresentationItem
{
    public IfcFaceBasedSurfaceModel()
    {
        fbsmFaces = new List<IfcConnectedFaceSet>();
    }
    public List<IfcConnectedFaceSet> fbsmFaces { get; set; }
}

public class IfcShell : IfcConnectedFaceSet
{
}
public class IfcOpenShell : IfcShell
{
}
public class IfcClosedShell : IfcShell
{
}
public class IfcConnectedFaceSet
{
    public IfcConnectedFaceSet()
    {
        cfsFaces = new List<IfcFace>();
    }
    public List<IfcFace> cfsFaces { get; set; }
}

public class IfcFace
{
    public IfcFace()
    {
        bounds = new List<IfcFaceBound>();
    }
    public List<IfcFaceBound> bounds { get; set; }
}


public class IfcFaceOuterBound : IfcFaceBound
{
}
public class IfcFaceBound
{
    public IfcLoop bound { get; set; }
}

public class IfcPolyLoop : IfcLoop
{
    public IfcPolyLoop()
    {
        polygon = new List<IfcCartesianPoint>();
    }
    public List<IfcCartesianPoint> polygon { get; set; }
}
public class IfcLoop
{
}

public class IfcCartesianPoint
{
    public IfcCartesianPoint()
    {
        coordinates = new List<Double>();
    }
    public List<Double> coordinates { get; set; }
}


public class IfcPolyline : IfcCurve
{
    public IfcPolyline()
    {
        points = new List<IfcCartesianPoint>();
    }
    public List<IfcCartesianPoint> points { get; set; }
}
public class IfcCurve : IfcGeometricRepresentationItem
{
}


public class IfcArbitraryProfileDefWithVoids : IfcArbitraryClosedProfileDef
{
    public IfcArbitraryProfileDefWithVoids()
    {
        innerCurves = new List<IfcCurve>();
    }
    public List<IfcCurve> innerCurves { get; set; }
}
public class IfcArbitraryClosedProfileDef : IfcProfileDef
{
    public IfcCurve outerCurve { get; set; }
}
public class IfcRectangleProfileDef : IfcParameterizedProfileDef
{
    public Double XDim { get; set; }
    public Double YDim { get; set; }
}
public class IfcParameterizedProfileDef : IfcProfileDef
{
    public IfcAxis2Placement2D position { get; set; }
}
public class IfcProfileDef
{
}

public class IfcDirection
{
    public IfcDirection()
    {
        directionRatios = new List<Double>();
    }
    public List<Double> directionRatios { get; set; }
}


public class ObjectLibraryResponse
{
    public ObjectLibraryResponse()
    {
        productLineProperties = new List<ProductLine>();
        representationItem = new List<IfcRepresentationItem>();
    }
    public String productLineType { get; set; }
    public String productLineName { get; set; }
    public String accept { get; set; }
    public List<ProductLine> productLineProperties { get; set; }
    public List<IfcRepresentationItem> representationItem { get; set; }
    public String ifcContent { get; set; }
    public String proprietaryContent { get; set; }
}
