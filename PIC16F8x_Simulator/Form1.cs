using Microsoft.VisualBasic.Devices;
using System.Data;
using System.Diagnostics.Eventing.Reader;
using System.Linq.Expressions;
using System.Reflection;
using System.Reflection.Metadata.Ecma335;
using System.Text;
using System.Windows.Forms;
using System.Xml;
using static System.Net.Mime.MediaTypeNames;
using static System.Windows.Forms.VisualStyles.VisualStyleElement;

namespace PIC16F8x_Simulator
{

    public partial class Form1 : Form
    {
        int[,] speicher = new int[32, 8];
        string[] code = new string[1024];
        int stackPointer = 0;
        int programmCounter = 0x00;
        float laufzeitzaehler = 0;  //hat ggf. Kommastellen
        float watchdog = 0; //hat ggf. Kommastellen
        int wRegister = 0x00;
        int vorteiler = 0x00;
        int maxVT = 0xff;
        bool WDTE = true;
        bool run = false;
        string[] executableLines = new string[8192];
        string[] stack1 = new string[8];

        float quarzFreq = 4;  //Standardfrequenz: 4MHz. Kommastellen möglich!
        bool fastRun = true;
        int zeile = 0;  //muss hier deklariert sein, wird für Breakpoint benötigt
        bool skip = false;  //wird für Brakepoint benötigt

        bool T0_interrupt = false;
        bool RB0_flanke = false;    //für RB0-Interrupt. Enthält bereits "richtige" Flanke
        bool RB4_7_changed = false;   //für RB4/RB5/RB6/RB7-Interrupt

        bool firstRefresh = true;
        bool sleepOn = false;

        private void getWRegister()
        {
            WReg_TextBox.Text = wRegister.ToString("X2");
        }

        private void getFSR()
        {
            FSRValue.Text = speicher[0, 4].ToString("X2");
        }

        private void setFSR()
        {
            speicher[16, 4] = speicher[0, 4]; // beide FSR gleich
        }

        private void getPCL()
        {
            PCLValue.Text = speicher[0, 2].ToString("X2");
        }

        private void getPCLATH()
        {
            PCLATHValue.Text = speicher[1, 2].ToString("X2");
        }

        private void getStatus()
        {
            StatusValue.Text = speicher[0, 3].ToString("X2");
        }

        private void getProgrammCounter()
        {
            PCValue.Text = programmCounter.ToString("X4");
        }

        private void getStackPointer()
        {
            StackpointerValue.Text = stackPointer.ToString();
        }

        private void getVorteiler()
        {
            VTValue.Text = (maxVT - vorteiler).ToString("X2");
        }

        private void watchdogTimerAktiv()
        {
            WDTECheckbox.Checked = WDTE;
        }

        private void getWatchdog()
        {
            WDTValue.Text = watchdog.ToString("F1") + " µs";
        }

        private void getLaufzeitzaehler()
        {
            laufZeit_label.Text = laufzeitzaehler.ToString("F1") + " µs";
        }

        private void pushStack(int toPush)
        {
            stack1[stackPointer] = Convert.ToString(toPush, 2).PadLeft(13, '0');
            stackPointer++;
            if (stackPointer > 7)
            {
                stackPointer = 0;
            }
        }

        private int popStack()
        {
            if (stackPointer > 0)
            {
                stackPointer--;
            }
            else if ((stackPointer == 0) && (stack1[7] != null))
            {
                stackPointer = 7;
            }

            int retval = Convert.ToInt16(stack1[stackPointer], 2);
            return retval;
        }

        private void getStack()
        {
            if (stack1[0] != null) stack0_label.Text = stack1[0]; else stack0_label.Text = "- / -";
            if (stack1[1] != null) stack1_label.Text = stack1[1]; else stack1_label.Text = "- / -";
            if (stack1[2] != null) stack2_label.Text = stack1[2]; else stack2_label.Text = "- / -";
            if (stack1[3] != null) stack3_label.Text = stack1[3]; else stack3_label.Text = "- / -";
            if (stack1[4] != null) stack4_label.Text = stack1[4]; else stack4_label.Text = "- / -";
            if (stack1[5] != null) stack5_label.Text = stack1[5]; else stack5_label.Text = "- / -";
            if (stack1[6] != null) stack6_label.Text = stack1[6]; else stack6_label.Text = "- / -";
            if (stack1[7] != null) stack7_label.Text = stack1[7]; else stack7_label.Text = "- / -";
        }

        private void InterruptFlag()
        {
            if (T0_interrupt)
            {
                speicher[1, 3] |= 0x04; //Timer-Interrupt-Flag setzen
                T0_interrupt = false;   //deaktivieren, damit nicht dauerhaft auslöst
            }

            if (RB0_flanke)
            {
                speicher[1, 3] |= 0x02; //INTF-Bit setzen (RB0-Interrupt)
                RB0_flanke = false;     //deaktivieren, damit nicht dauerhaft auslöst
            }

            if (RB4_7_changed)
            {
                speicher[1, 3] |= 0x01; //RBIF-Bit setzen
                RB4_7_changed = false;  //deaktivieren, damit nicht dauerhaft auslöst
            }
        }

        private void InterruptMaker()
        {
            InterruptFlag();    //muss hier auch stehen, da InterruptFlag() erst in UpdateUI() sonst aufgerufen werden würde, was idR nach timerIncrease() steht.
                                //Somit würde ohne diese Zeile ggf. ein Interrupt nicht ausgelöst werden.

            if (((speicher[1, 3] & 0x20) == 32) && ((speicher[1, 3] & 0x04) == 4)) //T0IE? Timer-Interrupt-Flag (T0-Interrupt)?
            {
                //kann NICHT aus SLEEP wecken!

                if ((speicher[1, 3] & 0x80) == 0x80)    //Prüfen, ob GIE (INTCON Bit 7) gesetzt ist, um Interrupts allgemein überhaupt an CPU weiterzuleiten
                {
                    pushStack(programmCounter); //Rückkehradresse auf Stack pushen
                    programmCounter = 0x04;    //Sprung zu Zeile 4, in der die ISRs stehen müssen

                    speicher[1, 3] &= 0b0111_1111;  //GIE Null setzen, damit nicht in Endlosschleife Interrupts ausgelöst werden

                    timerIncrease(2);   //Schreiben auf Stack + zu 04h springen -> 2 cycles
                }
            }

            if (((speicher[1, 3] & 0x10) == 16) && ((speicher[1, 3] & 0x02) == 2)) //INTE aktiviert? INTF (RB0-Interrupt)?
            {
                //theoretisch: aus SLEEP "aufwachen", WENN INTE vor SLEEP gesetzt wurde!
                if (sleepOn)
                {
                    sleepOn = false;
                    programmCounter++;
                }


                if ((speicher[1, 3] & 0x80) == 0x80)    //Prüfen, ob GIE (INTCON Bit 7) gesetzt ist, um Interrupts allgemein überhaupt an CPU weiterzuleiten
                {
                    pushStack(programmCounter); //Rückkehradresse auf Stack pushen
                    programmCounter = 0x04;    //Sprung zu Zeile 4, in der die ISRs stehen müssen

                    speicher[1, 3] &= 0b0111_1111;  //GIE Null setzen, damit nicht in Endlosschleife Interrupts ausgelöst werden

                    timerIncrease(2);   //Schreiben auf Stack + zu 04h springen -> 2 cycles
                }
            }

            if (((speicher[1, 3] & 0x08) == 8) && ((speicher[1, 3] & 0x01) == 1))  //RBIE? RBIF (RB7:4-Interrupt)?
            {
                //theoretisch: aus SLEEP "aufwachen"
                if (sleepOn)
                {
                    sleepOn = false;
                    programmCounter++;
                }

                if ((speicher[1, 3] & 0x80) == 0x80)    //Prüfen, ob GIE (INTCON Bit 7) gesetzt ist, um Interrupts allgemein überhaupt an CPU weiterzuleiten
                {
                    pushStack(programmCounter); //Rückkehradresse auf Stack pushen
                    programmCounter = 0x04;    //Sprung zu Zeile 4, in der die ISRs stehen müssen

                    speicher[1, 3] &= 0b0111_1111;  //GIE Null setzen, damit nicht in Endlosschleife Interrupts ausgelöst werden

                    timerIncrease(2);   //Schreiben auf Stack + zu 04h springen -> 2 cycles
                }
            }
        }

        private void updateUI()
        {
            InterruptFlag();
            showPC();
            MakeDataTable();
            RA();
            RB();
            ShowStatus();
            ShowOption();
            ShowINTCON();
            spezialRegisterShow();
            getStack();
        }

        private void spezialRegisterShow()
        {
            getWRegister();
            getFSR();
            getPCL();
            getPCLATH();
            getStatus();
            getProgrammCounter();
            getStackPointer();
            getVorteiler();
            watchdogTimerAktiv();
            getWatchdog();
            getLaufzeitzaehler();
        }

        private void readFile(string fileName)      //Datei einlesen
        {
            using (var fileStream = File.OpenRead(fileName))
            using (var streamReader = new StreamReader(fileStream, Encoding.UTF8, true, 128))
            {
                string line;
                int i = 0;
                while ((line = streamReader.ReadLine()) != null)
                {
                    code[i] = "";
                    code[i] = line;
                    i++;
                }
                output_file(i);

                stepButton.Enabled = true;
                runButton.Enabled = true;
                fastRun_checkBox.Enabled = true;
                resetButton.Enabled = true;
            }

        }

        private void output_file(int code_length)   //eingelesene Datei ausgeben
        {
            System.Data.DataTable code_table = new System.Data.DataTable();

            DataColumn column = new DataColumn();
            DataRow row;

            column.DataType = typeof(bool);
            column.ColumnName = "breakpoint";
            column.Caption = " ";
            column.ReadOnly = false;
            code_table.Columns.Add(column);

            column = new DataColumn();
            column.DataType = typeof(string);
            column.ColumnName = "Code";
            column.Caption = "Code";
            column.ReadOnly = true;
            code_table.Columns.Add(column);

            column = new DataColumn();
            column.DataType = typeof(bool);
            column.ColumnName = "Executable";
            column.Caption = "Executable";
            column.ReadOnly = true;
            code_table.Columns.Add(column);

            int j = 0; // zählen auf welche Position in executableLines geschrieben werden muss

            for (int i = 0; i <= code_length - 1; i++)
            {
                bool isExecutable = code[i].Substring(0, 1) != " ";

                row = code_table.NewRow();
                row[0] = false;
                row[1] = code[i];
                row[2] = isExecutable;
                code_table.Rows.Add(row);

                if (isExecutable && j < 8191)
                {

                    executableLines[j] = HexToBinary(code[i].Substring(5, 4)).PadLeft(14, '0'); // ausführbares Programm wird gespeichert
                    j++;
                }
            }

            code_dataGridView.Columns.Clear();
            code_dataGridView.DataSource = code_table;
            code_dataGridView.AllowUserToAddRows = false;
            code_dataGridView.RowHeadersVisible = false;
            highlightLine();
        }

        private void removeHighlightLine()
        {
            int lastExecZeile = findNextExecZeile();
            code_dataGridView.Rows[lastExecZeile].Cells[0].Style.BackColor = Color.White;
            code_dataGridView.Rows[lastExecZeile].Cells[1].Style.BackColor = Color.White;
            code_dataGridView.Rows[lastExecZeile].Cells[2].Style.BackColor = Color.White;
        }

        private void highlightLine()
        {
            int nextExecZeile = findNextExecZeile();
            code_dataGridView.Rows[nextExecZeile].Cells[0].Style.BackColor = Color.Red;
            code_dataGridView.Rows[nextExecZeile].Cells[1].Style.BackColor = Color.Red;
            code_dataGridView.Rows[nextExecZeile].Cells[2].Style.BackColor = Color.Red;
            code_dataGridView.FirstDisplayedScrollingRowIndex = nextExecZeile;
        }

        private int findNextExecZeile()
        {
            zeile = 0;
            int execZeile = 0;

            while ((programmCounter >= execZeile) && (zeile < code_dataGridView.RowCount - 1))
            {
                if ((programmCounter > execZeile) && Convert.ToBoolean(code_dataGridView.Rows[zeile].Cells[2].Value))   //noch nicht richtige Zeile gefunden, Zeile ist aber ausführbar
                {
                    execZeile++;
                    zeile++;
                }
                else if (programmCounter > execZeile)   //noch nicht richtige Zeile gefunden
                {
                    zeile++;
                }
                else if ((programmCounter == execZeile) && Convert.ToBoolean(code_dataGridView.Rows[zeile].Cells[2].Value)) // richtige Zeile gefunden
                {
                    break;
                }
                else if (programmCounter == execZeile)  //Suche nach der "richtigen" Zeile, die ausführbar ist
                {
                    zeile++;
                }
            }
            return zeile;
        }

        private void MakeDataTable()        //Erstellen der anzuzeigenden Tabelle des Speichers
        {
            System.Data.DataTable speicherTabelle = new System.Data.DataTable();

            DataColumn column = new DataColumn();
            DataRow row;

            column.DataType = typeof(string);
            column.ColumnName = " ";
            column.Caption = " ";
            column.ReadOnly = true;
            speicherTabelle.Columns.Add(column);

            column = new DataColumn();
            column.DataType = typeof(string);
            column.ColumnName = "00";
            column.Caption = "00";
            speicherTabelle.Columns.Add(column);

            column = new DataColumn();
            column.DataType = typeof(string);
            column.ColumnName = "01";
            column.Caption = "01";
            speicherTabelle.Columns.Add(column);

            column = new DataColumn();
            column.DataType = typeof(string);
            column.ColumnName = "02";
            column.Caption = "02";
            speicherTabelle.Columns.Add(column);

            column = new DataColumn();
            column.DataType = typeof(string);
            column.ColumnName = "03";
            column.Caption = "03";
            speicherTabelle.Columns.Add(column);

            column = new DataColumn();
            column.DataType = typeof(string);
            column.ColumnName = "04";
            column.Caption = "04";
            speicherTabelle.Columns.Add(column);

            column = new DataColumn();
            column.DataType = typeof(string);
            column.ColumnName = "05";
            column.Caption = "05";
            speicherTabelle.Columns.Add(column);

            column = new DataColumn();
            column.DataType = typeof(string);
            column.ColumnName = "06";
            column.Caption = "06";
            speicherTabelle.Columns.Add(column);

            column = new DataColumn();
            column.DataType = typeof(string);
            column.ColumnName = "07";
            column.Caption = "07";
            speicherTabelle.Columns.Add(column);


            for (int i = 0; i < 32; i++)
            {
                row = speicherTabelle.NewRow();
                row[0] = (i * 8).ToString("X2");
                row[1] = speicher[i, 0].ToString("X2");
                row[2] = speicher[i, 1].ToString("X2");
                row[3] = speicher[i, 2].ToString("X2");
                row[4] = speicher[i, 3].ToString("X2");
                row[5] = speicher[i, 4].ToString("X2");
                row[6] = speicher[i, 5].ToString("X2");
                row[7] = speicher[i, 6].ToString("X2");
                row[8] = speicher[i, 7].ToString("X2");
                speicherTabelle.Rows.Add(row);
            }

            //Tabelle (Speicher) anzeigen:
            dataGridView1.Columns.Clear();
            dataGridView1.DataSource = speicherTabelle;
            dataGridView1.AllowUserToAddRows = false;
            dataGridView1.AutoResizeRowHeadersWidth(DataGridViewRowHeadersWidthSizeMode.AutoSizeToAllHeaders);
        }

        private void RA()
        {
            if ((speicher[16, 5] & 0x80) == 128) TrisA7_label.Text = "i";   //bei Input darf der im PORTA gespeicherte Wert (eventuell intern geänderte Wert) nicht angezeigt werden, nur bei...
            else //...output. Dann muss der "von innen" angelegte Wert angezeigt werden
            {
                TrisA7_label.Text = "o";
                PinA7_button.Text = ((speicher[0, 5] & 0x80) / 0x80).ToString("X");
            }
            if ((speicher[16, 5] & 0x40) == 64) TrisA6_label.Text = "i";
            else
            {
                TrisA6_label.Text = "o";
                PinA6_button.Text = ((speicher[0, 5] & 0x40) / 0x40).ToString("X");
            }
            if ((speicher[16, 5] & 0x20) == 32) TrisA5_label.Text = "i";
            else
            {
                TrisA5_label.Text = "o";
                PinA5_button.Text = ((speicher[0, 5] & 0x20) / 0x20).ToString("X");
            }
            if ((speicher[16, 5] & 0x10) == 16) TrisA4_label.Text = "i";
            else
            {
                TrisA4_label.Text = "o";
                PinA4_button.Text = ((speicher[0, 5] & 0x10) / 0x10).ToString("X");
            }
            if ((speicher[16, 5] & 0x08) == 8) TrisA3_label.Text = "i";
            else
            {
                TrisA3_label.Text = "o";
                PinA3_button.Text = ((speicher[0, 5] & 0x08) / 0x08).ToString("X");
            }
            if ((speicher[16, 5] & 0x04) == 4) TrisA2_label.Text = "i";
            else
            {
                TrisA2_label.Text = "o";
                PinA2_button.Text = ((speicher[0, 5] & 0x04) / 0x04).ToString("X");
            }
            if ((speicher[16, 5] & 0x02) == 2) TrisA1_label.Text = "i";
            else
            {
                TrisA1_label.Text = "o";
                PinA1_button.Text = ((speicher[0, 5] & 0x02) / 0x02).ToString("X");
            }
            if ((speicher[16, 5] & 0x01) == 1) TrisA0_label.Text = "i";
            else
            {
                TrisA0_label.Text = "o";
                PinA0_button.Text = ((speicher[0, 5] & 0x01) / 0x01).ToString("X");
            }

            //beim ersten Mal starten/nach einem Reset sollen die Werte aber angezeigt werden. RA wird vor RB ausgeführt, daher wird hier "firstRefresh" nicht auf false gesetzt.
            if (firstRefresh)
            {
                PinA7_button.Text = ((speicher[0, 5] & 0x80) / 0x80).ToString("X");
                PinA6_button.Text = ((speicher[0, 5] & 0x40) / 0x40).ToString("X");
                PinA5_button.Text = ((speicher[0, 5] & 0x20) / 0x20).ToString("X");
                PinA4_button.Text = ((speicher[0, 5] & 0x10) / 0x10).ToString("X");
                PinA3_button.Text = ((speicher[0, 5] & 0x08) / 0x08).ToString("X");
                PinA2_button.Text = ((speicher[0, 5] & 0x04) / 0x04).ToString("X");
                PinA1_button.Text = ((speicher[0, 5] & 0x02) / 0x02).ToString("X");
                PinA0_button.Text = ((speicher[0, 5] & 0x01) / 0x01).ToString("X");
            }
        }

        private void RB()
        {
            if ((speicher[16, 6] & 0x80) == 128) TrisB7_label.Text = "i";   //bei Input darf der im PORTB gespeicherte Wert (eventuell intern geänderte Wert) nicht angezeigt werden, nur bei...
            else //...output. Dann muss der "von innen" angelegte Wert angezeigt werden
            {
                TrisB7_label.Text = "o";
                PinB7_button.Text = ((speicher[0, 6] & 0x80) / 0x80).ToString("X");
            }
            if ((speicher[16, 6] & 0x40) == 64) TrisB6_label.Text = "i";
            else
            {
                TrisB6_label.Text = "o";
                PinB6_button.Text = ((speicher[0, 6] & 0x40) / 0x40).ToString("X");
            }
            if ((speicher[16, 6] & 0x20) == 32) TrisB5_label.Text = "i";
            else
            {
                TrisB5_label.Text = "o";
                PinB5_button.Text = ((speicher[0, 6] & 0x20) / 0x20).ToString("X");
            }
            if ((speicher[16, 6] & 0x10) == 16) TrisB4_label.Text = "i";
            else
            {
                TrisB4_label.Text = "o";
                PinB4_button.Text = ((speicher[0, 6] & 0x10) / 0x10).ToString("X");
            }
            if ((speicher[16, 6] & 0x08) == 8) TrisB3_label.Text = "i";
            else
            {
                TrisB3_label.Text = "o";
                PinB3_button.Text = ((speicher[0, 6] & 0x08) / 0x08).ToString("X");
            }
            if ((speicher[16, 6] & 0x04) == 4) TrisB2_label.Text = "i";
            else
            {
                TrisB2_label.Text = "o";
                PinB2_button.Text = ((speicher[0, 6] & 0x04) / 0x04).ToString("X");
            }
            if ((speicher[16, 6] & 0x02) == 2) TrisB1_label.Text = "i";
            else
            {
                TrisB1_label.Text = "o";
                PinB1_button.Text = ((speicher[0, 6] & 0x02) / 0x02).ToString("X");
            }
            if ((speicher[16, 6] & 0x01) == 1) TrisB0_label.Text = "i";
            else
            {
                TrisB0_label.Text = "o";
                PinB0_button.Text = ((speicher[0, 6] & 0x01) / 0x01).ToString("X");
            }

            //beim ersten Mal starten/nach einem Reset sollen die Werte aber angezeigt werden. Hier wird "firstRefresh" auf false gesetzt, um in den kommenden Prüfungen keine Fehler hervorzubringen.
            if (firstRefresh)
            {
                PinB7_button.Text = ((speicher[0, 6] & 0x80) / 0x80).ToString("X");
                PinB6_button.Text = ((speicher[0, 6] & 0x40) / 0x40).ToString("X");
                PinB5_button.Text = ((speicher[0, 6] & 0x20) / 0x20).ToString("X");
                PinB4_button.Text = ((speicher[0, 6] & 0x10) / 0x10).ToString("X");
                PinB3_button.Text = ((speicher[0, 6] & 0x08) / 0x08).ToString("X");
                PinB2_button.Text = ((speicher[0, 6] & 0x04) / 0x04).ToString("X");
                PinB1_button.Text = ((speicher[0, 6] & 0x02) / 0x02).ToString("X");
                PinB0_button.Text = ((speicher[0, 6] & 0x01) / 0x01).ToString("X");

                firstRefresh = false;
            }
        }

        public Form1()
        {
            InitializeComponent();
            onReset(0);
            updateUI();
            quarzFreq_comboBox.SelectedIndex = 6;
        }

        private void browseFile_Button_Click(object sender, EventArgs e)
        {
            DialogResult browse_result = this.openFileDialog1.ShowDialog();     //zeigt das Dialogfeld zum Browsen der Datei an. Wird das Dialogfeld mit einer Auswahl geschlossen, wird ein bestimmter Wert zurückgegeben ("DialogResult.Ok").
            if (browse_result == DialogResult.OK)        //Anzeigen der ausgewählten Datei im Textfeld, sobald eine Datei erfolgreich ausgewählt wurde
            {
                this.fileName_textBox.Text = this.openFileDialog1.FileName;

                executableReset(); // initialisieren vor readfile
                readFile(this.openFileDialog1.FileName);
                onReset(0);     // Power-on Reset beim Laden neuer Dateien
                resetFile();
                updateUI();
            }
        }

        private void executableReset()
        {
            for (int i = 0; i < 256; i++)
            {
                executableLines[i] = null;
            }
            programmCounter = 0x00;
        }

        public void onReset(int resettype)
        {
            firstRefresh = true;

            switch (resettype)
            {
                case 0: //Power-on Reset
                    // Bank 0
                    // speicher[0, 0] = 0xkein Wert;    // INDF
                    speicher[0, 1] = 0x00;      // TMR0
                    speicher[0, 2] = 0x00;      // PCL
                    speicher[0, 3] = 0x18;      // STATUS
                    speicher[0, 4] = 0x00;      // FSR
                    speicher[0, 5] = 0x00;      // PORTA
                    speicher[0, 6] = 0x80;      // PORTB
                    speicher[0, 7] = 0x00;      // unimplemented location read as '0'
                    speicher[1, 0] = 0x80;      // EEDATA
                    speicher[1, 1] = 0x80;      // EEADR
                    speicher[1, 2] = 0x00;      // PCLATH
                    speicher[1, 3] = 0x00;      // INTCON

                    // Bank 1
                    // speicher[16, 0] = 0xkein Wert;   // INDF
                    speicher[16, 1] = 0xff;      // OPTION_REG
                    speicher[16, 2] = 0x00;      // PCL
                    speicher[16, 3] = 0x18;      // STATUS
                    speicher[16, 4] = 0x00;      // FSR
                    speicher[16, 5] = 0xff;      // TRISA
                    speicher[16, 6] = 0xff;      // TRISB
                    speicher[16, 7] = 0x00;      // unimplemented location read as '0'
                    speicher[17, 0] = 0x00;      // EECON1
                    speicher[17, 1] = 0x00;      // EECON2
                    speicher[17, 2] = 0x00;      // PCLATH
                    speicher[17, 3] = 0x00;      // INTCON
                    break;

                case 1: //!MCLR Reset
                    // Bank 0
                    // speicher[0, 0] = 0xkein Wert;    // INDF
                    // speicher[0, 1] = 0xff;      // TMR0 unverändert
                    speicher[0, 2] = 0x00;      // PCL
                    speicher[0, 3] = speicher[0, 3] & 0x1f; // STATUS
                    // speicher[0, 4] = 0xff;      // FSR unverändert
                    // speicher[0, 5] = 0x1f;      // PORTA unverändert
                    // speicher[0, 6] = 0xff;      // PORTB unverändert
                    speicher[0, 7] = 0x00;      // unimplemented location read as '0'
                    // speicher[1, 0] = 0xff;      // EEDATA unverändert
                    // speicher[1, 1] = 0xff;      // EEADR unverändert
                    speicher[1, 2] = 0x00;      // PCLATH
                    speicher[1, 3] = speicher[1, 3] & 0x01; // INTCON

                    // Bank 1
                    // speicher[16, 0] = 0xkein Wert;   // INDF
                    speicher[16, 1] = 0x00;      // OPTION_REG
                    speicher[16, 2] = speicher[0, 2];  // PCL
                    speicher[16, 3] = speicher[0, 3]; // STATUS
                    // speicher[16, 4] = 0x1f;      // FSR unverändert
                    speicher[16, 5] = 0x1f;      // TRISA
                    speicher[16, 6] = 0xff;      // TRISB
                    speicher[16, 7] = 0x00;      // unimplemented location read as '0'
                    speicher[17, 0] = speicher[17, 0] & 0x08;      // EECON1
                    speicher[17, 1] = 0x00;      // EECON2
                    speicher[17, 2] = 0x00;      // PCLATH
                    speicher[17, 3] = speicher[17, 3] & 0x01;      // INTCON
                    break;

                case 2: //other resets
                    // Bank 0
                    // speicher[0, 0] = 0xkein Wert;    // INDF
                    // speicher[0, 1] = 0xff;      // TMR0 unverändert
                    speicher[0, 2] = 0x00;      // PCL
                    speicher[0, 3] = speicher[0, 3] & 0x1f; // STATUS
                    // speicher[0, 4] = 0xff;      // FSR unverändert
                    // speicher[0, 5] = 0x1f;      // PORTA unverändert
                    // speicher[0, 6] = 0xff;      // PORTB unverändert
                    speicher[0, 7] = 0x00;      // unimplemented location read as '0'
                    // speicher[1, 0] = 0xff;      // EEDATA unverändert
                    // speicher[1, 1] = 0xff;      // EEADR unverändert
                    speicher[1, 2] = 0x00;      // PCLATH
                    speicher[1, 3] = speicher[1, 3] & 0x01; // INTCON

                    // Bank 1
                    // speicher[16, 0] = 0xkein Wert;   // INDF
                    speicher[16, 1] = 0x00;      // OPTION_REG
                    speicher[16, 2] = speicher[0, 2]; // PCL
                    speicher[16, 3] = speicher[0, 3]; // STATUS
                    // speicher[16, 4] = 0x1f;      // FSR unverändert
                    speicher[16, 5] = 0x1f;      // TRISA
                    speicher[16, 6] = 0xff;      // TRISB
                    speicher[16, 7] = 0x00;      // unimplemented location read as '0'
                    speicher[17, 0] = speicher[17, 0] & 0x08;      // EECON1
                    speicher[17, 1] = 0x00;      // EECON2
                    speicher[17, 2] = 0x00;      // PCLATH
                    speicher[17, 3] = speicher[17, 3] & 0x01;      // INTCON
                    break;
            }

        }

        public static string HexToBinary(string hexString)
        {
            int hexValue = int.Parse(hexString, System.Globalization.NumberStyles.HexNumber);
            string binaryString = Convert.ToString(hexValue, 2).PadLeft(8, '0');
            return binaryString;
        }

        public static string BinaryToHex(string binaryString)
        {
            // Führende Nullen auffüllen, um sicherzustellen, dass der Binär-String eine Länge von 16 hat
            binaryString = binaryString.PadLeft(16, '0');

            // Binär-String in Byte-Array umwandeln
            byte[] bytes = new byte[2];
            bytes[0] = Convert.ToByte(binaryString.Substring(0, 8), 2);
            bytes[1] = Convert.ToByte(binaryString.Substring(8, 8), 2);

            // Byte-Array in Hexadezimal-String umwandeln
            string hexString = BitConverter.ToString(bytes).Replace("-", "");

            return hexString;
        }

        private void identifyCommand(string commandbin) // which command?
        {
            switch (commandbin)
            {
                // Byteorientiert
                case string x when x.StartsWith("000111"):
                    ADDWF(commandbin.Substring(6, 8));
                    break;
                case string x when x.StartsWith("000101"):
                    ANDWF(commandbin.Substring(6, 8));
                    break;
                case string x when x.StartsWith("0000011"):
                    CLRF(commandbin.Substring(7, 7));
                    break;
                case string x when x.StartsWith("0000010"):
                    CLRW();
                    break;
                case string x when x.StartsWith("001001"):
                    COMF(commandbin.Substring(6, 8));
                    break;
                case string x when x.StartsWith("000011"):
                    DECF(commandbin.Substring(6, 8));
                    break;
                case string x when x.StartsWith("001011"):
                    DECFSZ(commandbin.Substring(6, 8));
                    break;
                case string x when x.StartsWith("001010"):
                    INCF(commandbin.Substring(6, 8));
                    break;
                case string x when x.StartsWith("001111"):
                    INCFSZ(commandbin.Substring(6, 8));
                    break;
                case string x when x.StartsWith("000100"):
                    IORWF(commandbin.Substring(6, 8));
                    break;
                case string x when x.StartsWith("001000"):
                    MOVF(commandbin.Substring(6, 8));
                    break;
                case string x when x.StartsWith("0000001"):
                    MOVWF(commandbin.Substring(7, 7));
                    break;
                case string x when x.StartsWith("0000000") && x.EndsWith("00000"):
                    NOP();
                    break;
                case string x when x.StartsWith("001101"):
                    RLF(commandbin.Substring(6, 8));
                    break;
                case string x when x.StartsWith("001100"):
                    RRF(commandbin.Substring(6, 8));
                    break;
                case string x when x.StartsWith("000010"):
                    SUBWF(commandbin.Substring(6, 8));
                    break;
                case string x when x.StartsWith("001110"):
                    SWAPF(commandbin.Substring(6, 8));
                    break;
                case string x when x.StartsWith("000110"):
                    XORWF(commandbin.Substring(6, 8));
                    break;
                // Bitorientiert
                case string x when x.StartsWith("0100"):
                    BCF(commandbin.Substring(4, 10));
                    break;
                case string x when x.StartsWith("0101"):
                    BSF(commandbin.Substring(4, 10));
                    break;
                case string x when x.StartsWith("0110"):
                    BTFSC(commandbin.Substring(4, 10));
                    break;
                case string x when x.StartsWith("0111"):
                    BTFSS(commandbin.Substring(4, 10));
                    break;
                // Literal und Steuerbefehle
                case string x when x.StartsWith("11111"):
                    ADDLW(commandbin.Substring(6, 8));
                    break;
                case string x when x.StartsWith("111001"):
                    ANDLW(commandbin.Substring(6, 8));
                    break;
                case string x when x.StartsWith("100"):
                    CALL(commandbin.Substring(3, 11));
                    break;
                case string x when x.StartsWith("00000001100100"):
                    CLRWDT();
                    break;
                case string x when x.StartsWith("101"):
                    GOTO(commandbin.Substring(3, 11));
                    break;
                case string x when x.StartsWith("111000"):
                    IORLW(commandbin.Substring(6, 8));
                    break;
                case string x when x.StartsWith("1100"):
                    MOVLW(commandbin.Substring(6, 8));
                    break;
                case string x when x.StartsWith("00000000001001"):
                    RETFIE();
                    break;
                case string x when x.StartsWith("1101"):
                    RETLW(commandbin.Substring(6, 8));
                    break;
                case string x when x.StartsWith("00000000001000"):
                    RETURN();
                    break;
                case string x when x.StartsWith("00000001100011"):
                    SLEEP();
                    break;
                case string x when x.StartsWith("11110"):
                    SUBLW(commandbin.Substring(6, 8));
                    break;
                case string x when x.StartsWith("111010"):
                    XORLW(commandbin.Substring(6, 8));
                    break;
                default:
                    break;
            }
        }

        private void ADDWF(string file_d)
        {
            int d = int.Parse(file_d.Substring(0, 1));
            int fileValue = int.Parse(BinaryToHex(file_d.Substring(1, 7)), System.Globalization.NumberStyles.HexNumber);
            int row = 0, column = 0;

            if (fileValue == 0) // indirekte Adressierung
            {
                row = speicher[0, 4] / 8;
                column = speicher[0, 4] % 8;
            }
            else
            {
                row = fileValue / 8;
                column = fileValue % 8;
            }

            AddDigitCarry(wRegister, speicher[row, column]);
            int summe = wRegister + speicher[row, column];
            if (d == 0)
            {
                wRegister = summe;
            }
            else if (d == 1)
            {
                if ((speicher[0, 3] & 0b00100000) == 32) // wenn RP0 gesetzt Bank1
                {
                    row = row + 16;
                    if ((row == 16 && column == 0) || (row == 16 && column == 2) || (row == 16 && column == 3) || (row == 16 && column == 4) || (row == 17 && column == 2) || (row == 17 && column == 3))
                    {
                        row = row - 16;
                    }
                }
                speicher[row, column] = summe;
                if ((row == 0) && (column == 2)) // falls f = PCL muss PC erhöht werden weil PCL von PC gebildet wird
                {
                    int IncPCL = speicher[0, 2] + 1;
                    string PCL = Convert.ToString(IncPCL, 2).PadLeft(8, '0');
                    string PCLATH = Convert.ToString(speicher[1, 2], 2).PadLeft(8, '0');

                    string newPC = PCLATH.Substring(3) + PCL;
                    programmCounter = int.Parse(BinaryToHex(newPC.PadLeft(4, '0')), System.Globalization.NumberStyles.HexNumber);
                    programmCounter--;
                }
            }

            if (row == 16 && column == 1)
            {
                VTcheck();
            }
            Carry(); // wenn carry gesetzt wregister -256 -> in Carry()
            if (summe != 0)
            {
                speicher[0, 3] &= 0b11111011; // zero nicht gesetzt
            }
            programmCounter++;
            timerIncrease(1);
            updateUI();
        }

        private void ANDWF(string file_d)
        {
            int d = int.Parse(file_d.Substring(0, 1));
            int fileValue = int.Parse(BinaryToHex(file_d.Substring(1, 7)), System.Globalization.NumberStyles.HexNumber);
            int row = fileValue / 8;
            int column = fileValue % 8;

            int add = wRegister & speicher[row, column];
            int hexadd = int.Parse(add.ToString("X"), System.Globalization.NumberStyles.HexNumber);
            if (d == 0)
            {
                wRegister = hexadd;
            }
            else if (d == 1)
            {
                if ((speicher[0, 3] & 0b00100000) == 32) // wenn RP0 gesetzt Bank1
                {
                    row = row + 16;
                    if ((row == 16 && column == 0) || (row == 16 && column == 2) || (row == 16 && column == 3) || (row == 16 && column == 4) || (row == 17 && column == 2) || (row == 17 && column == 3))
                    {
                        row = row - 16;
                    }
                }
                speicher[row, column] = hexadd;
            }

            if (row == 16 && column == 1)
            {
                VTcheck();
            }
            Zero();
            programmCounter++;
            timerIncrease(1);
            updateUI();
        }

        private void CLRF(string file)
        {
            int fileValue = int.Parse(BinaryToHex(file), System.Globalization.NumberStyles.HexNumber);
            int row = fileValue / 8;
            int column = fileValue % 8;

            if ((speicher[0, 3] & 0b00100000) == 32) // wenn Rp0 gesetzt Bank1
            {
                row = row + 16;
                if ((row == 16 && column == 0) || (row == 16 && column == 2) || (row == 16 && column == 3) || (row == 16 && column == 4) || (row == 17 && column == 2) || (row == 17 && column == 3))
                {
                    row = row - 16;
                }
            }

            speicher[row, column] = 0x00;
            speicher[0, 3] |= 0b00000100; // zero gesetzt
            speicher[16, 3] = speicher[0, 3];
            if (row == 16 && column == 1)
            {
                VTcheck();
            }
            programmCounter++;
            timerIncrease(1);
            updateUI();
        }

        private void CLRW()
        {
            wRegister = 0x00;
            speicher[0, 3] |= 0b00000100; // zero gesetzt
            speicher[16, 3] = speicher[0, 3];
            programmCounter++;
            timerIncrease(1);
            updateUI();
        }

        private void COMF(string file_d)
        {
            int d = int.Parse(file_d.Substring(0, 1));
            int fileValue = int.Parse(BinaryToHex(file_d.Substring(1, 7)), System.Globalization.NumberStyles.HexNumber);
            int row = fileValue / 8;
            int column = fileValue % 8;

            int complement = ~speicher[row, column]; // complement bilden
            int hexcomplement = (byte)int.Parse(complement.ToString("X"), System.Globalization.NumberStyles.HexNumber);
            if (d == 0)
            {
                wRegister = hexcomplement;
            }
            else if (d == 1)
            {
                if ((speicher[0, 3] & 0b00100000) == 32) // wenn RP0 gesetzt Bank1
                {
                    row = row + 16;
                    if ((row == 16 && column == 0) || (row == 16 && column == 2) || (row == 16 && column == 3) || (row == 16 && column == 4) || (row == 17 && column == 2) || (row == 17 && column == 3))
                    {
                        row = row - 16;
                    }
                }
                speicher[row, column] = hexcomplement;
            }

            if (row == 16 && column == 1)
            {
                VTcheck();
            }
            Zero();
            programmCounter++;
            timerIncrease(1);
            updateUI();
        }

        private void DECF(string file_d)
        {
            int d = int.Parse(file_d.Substring(0, 1));
            int fileValue = int.Parse(BinaryToHex(file_d.Substring(1, 7)), System.Globalization.NumberStyles.HexNumber);
            int row = fileValue / 8;
            int column = fileValue % 8;

            int decValue = (byte)(speicher[row, column] - 1);
            int hexdecValue = (byte)int.Parse(decValue.ToString("X"), System.Globalization.NumberStyles.HexNumber);
            if (d == 0)
            {
                wRegister = hexdecValue;
            }
            else if (d == 1)
            {
                if ((speicher[0, 3] & 0b00100000) == 32) // wenn RP0 gesetzt Bank1
                {
                    row = row + 16;
                    if ((row == 16 && column == 0) || (row == 16 && column == 2) || (row == 16 && column == 3) || (row == 16 && column == 4) || (row == 17 && column == 2) || (row == 17 && column == 3))
                    {
                        row = row - 16;
                    }
                }
                speicher[row, column] = hexdecValue;
            }

            if (row == 16 && column == 1)
            {
                VTcheck();
            }
            Zero();
            programmCounter++;
            timerIncrease(1);
            updateUI();
        }

        private void DECFSZ(string file_d)
        {
            int d = int.Parse(file_d.Substring(0, 1));
            int fileValue = int.Parse(BinaryToHex(file_d.Substring(1, 7)), System.Globalization.NumberStyles.HexNumber);
            int row = fileValue / 8;
            int column = fileValue % 8;

            int decValue = (byte)(speicher[row, column] - 1);
            int hexdecValue = (byte)int.Parse(decValue.ToString("X"), System.Globalization.NumberStyles.HexNumber);
            if (d == 0)
            {
                wRegister = hexdecValue;
            }
            else if (d == 1)
            {
                if ((speicher[0, 3] & 0b00100000) == 32) // wenn RP0 gesetzt Bank1
                {
                    row = row + 16;
                    if ((row == 16 && column == 0) || (row == 16 && column == 2) || (row == 16 && column == 3) || (row == 16 && column == 4) || (row == 17 && column == 2) || (row == 17 && column == 3))
                    {
                        row = row - 16;
                    }
                }
                speicher[row, column] = hexdecValue;
            }

            if (hexdecValue == 0)
            {
                NOP();
            }

            if (row == 16 && column == 1)
            {
                VTcheck();
            }
            programmCounter++;
            timerIncrease(1);
            updateUI();
        }

        private void INCF(string file_d)
        {
            int d = int.Parse(file_d.Substring(0, 1));
            int fileValue = int.Parse(BinaryToHex(file_d.Substring(1, 7)), System.Globalization.NumberStyles.HexNumber);
            int row = 0, column = 0;

            if (fileValue == 0) // indirekte Adressierung
            {
                row = speicher[0, 4] / 8;
                column = speicher[0, 4] % 8;
            }
            else
            {
                row = fileValue / 8;
                column = fileValue % 8;
            }

            int incValue = (byte)(speicher[row, column] + 1);
            int hexincValue = (byte)int.Parse(incValue.ToString("X"), System.Globalization.NumberStyles.HexNumber);
            if (d == 0)
            {
                wRegister = hexincValue;
            }
            else if (d == 1)
            {
                if ((speicher[0, 3] & 0b00100000) == 32) // wenn RP0 gesetzt Bank1
                {
                    row = row + 16;
                    if ((row == 16 && column == 0) || (row == 16 && column == 2) || (row == 16 && column == 3) || (row == 16 && column == 4) || (row == 17 && column == 2) || (row == 17 && column == 3))
                    {
                        row = row - 16;
                    }
                }
                speicher[row, column] = hexincValue;
            }

            if (row == 16 && column == 1)
            {
                VTcheck();
            }
            Zero();
            programmCounter++;
            timerIncrease(1);
            updateUI();
        }

        private void INCFSZ(string file_d)
        {
            int d = int.Parse(file_d.Substring(0, 1));
            int fileValue = int.Parse(BinaryToHex(file_d.Substring(1, 7)), System.Globalization.NumberStyles.HexNumber);
            int row = fileValue / 8;
            int column = fileValue % 8;

            int incValue = (byte)(speicher[row, column] + 1);
            int hexincValue = (byte)int.Parse(incValue.ToString("X"), System.Globalization.NumberStyles.HexNumber);
            if (d == 0)
            {
                wRegister = hexincValue;
            }
            else if (d == 1)
            {
                if ((speicher[0, 3] & 0b00100000) == 32) // wenn RP0 gesetzt Bank1
                {
                    row = row + 16;
                    if ((row == 16 && column == 0) || (row == 16 && column == 2) || (row == 16 && column == 3) || (row == 16 && column == 4) || (row == 17 && column == 2) || (row == 17 && column == 3))
                    {
                        row = row - 16;
                    }
                }
                speicher[row, column] = hexincValue;
            }

            if (hexincValue == 0)
            {
                NOP();
            }

            if (row == 16 && column == 1)
            {
                VTcheck();
            }
            programmCounter++;
            timerIncrease(1);
            updateUI();
        }

        private void IORWF(string file_d)
        {
            int d = int.Parse(file_d.Substring(0, 1));
            int fileValue = int.Parse(BinaryToHex(file_d.Substring(1, 7)), System.Globalization.NumberStyles.HexNumber);
            int row = fileValue / 8;
            int column = fileValue % 8;

            int or = wRegister | speicher[row, column];
            int hexor = int.Parse(or.ToString("X"), System.Globalization.NumberStyles.HexNumber);
            if (d == 0)
            {
                wRegister = hexor;
            }
            else if (d == 1)
            {
                if ((speicher[0, 3] & 0b00100000) == 32) // wenn RP0 gesetzt Bank1
                {
                    row = row + 16;
                    if ((row == 16 && column == 0) || (row == 16 && column == 2) || (row == 16 && column == 3) || (row == 16 && column == 4) || (row == 17 && column == 2) || (row == 17 && column == 3))
                    {
                        row = row - 16;
                    }
                }
                speicher[row, column] = hexor;
            }

            if (row == 16 && column == 1)
            {
                VTcheck();
            }
            Zero();
            programmCounter++;
            timerIncrease(1);
            updateUI();
        }

        private void MOVF(string file_d)
        {
            int d = int.Parse(file_d.Substring(0, 1));
            int fileValue = int.Parse(BinaryToHex(file_d.Substring(1, 7)), System.Globalization.NumberStyles.HexNumber);
            int row = fileValue / 8;
            int column = fileValue % 8;

            int file = int.Parse(speicher[row, column].ToString("X"), System.Globalization.NumberStyles.HexNumber);
            if (d == 0)
            {
                wRegister = file;
                Zero();
            }
            else if (d == 1)
            {
                if ((speicher[0, 3] & 0b00100000) == 32) // wenn RP0 gesetzt Bank1
                {
                    row = row + 16;
                    if ((row == 16 && column == 0) || (row == 16 && column == 2) || (row == 16 && column == 3) || (row == 16 && column == 4) || (row == 17 && column == 2) || (row == 17 && column == 3))
                    {
                        row = row - 16;
                    }
                }
                speicher[row, column] = file;
                speicher[0, 3] |= 0b00000100; // zero gesetzt
                speicher[16, 3] = speicher[0, 3];
            }

            if (row == 16 && column == 1)
            {
                vorteiler++;
                VTcheck();
            }
            programmCounter++;
            timerIncrease(1);
            updateUI();
        }

        private void MOVWF(string file)
        {
            int fileValue = int.Parse(BinaryToHex(file), System.Globalization.NumberStyles.HexNumber);
            int row = 0, column = 0;

            if (fileValue == 0) // indirekte Adressierung
            {
                row = speicher[0, 4] / 8;
                column = speicher[0, 4] % 8;
            }
            else
            {
                row = fileValue / 8;
                column = fileValue % 8;
            }
            if ((speicher[0, 3] & 0b00100000) == 32) // wenn RP0 gesetzt Bank1
            {
                row = row + 16;
                if ((row == 16 && column == 0) || (row == 16 && column == 2) || (row == 16 && column == 3) || (row == 16 && column == 4) || (row == 17 && column == 2) || (row == 17 && column == 3))
                {
                    row = row - 16;
                }
            }

            speicher[row, column] = wRegister;
            if (row == 16 && column == 1)
            {
                if ((HexToBinary(Convert.ToString(speicher[16, 1], 16))).Substring(4, 1) == "0") // PSA-Bit
                {
                    vorteiler = 0;
                    VTcheck();
                }
            }
            if (row == 0 && column == 1)
            {
                vorteiler = 0;
                maxVT = setVT();
            }
            timerIncrease(1);
            programmCounter++;
            updateUI();
        }

        private void NOP()
        {
            programmCounter++;
            timerIncrease(1);
            updateUI();
        }

        private void RLF(string file_d)
        {
            int d = int.Parse(file_d.Substring(0, 1));
            int file = int.Parse(BinaryToHex(file_d.Substring(1, 7)), System.Globalization.NumberStyles.HexNumber);
            int row = 0, column = 0;

            if (file == 0)
            {
                row = speicher[0, 4] / 8;
                column = speicher[0, 4] % 8;
            }
            else
            {
                row = file / 8;
                column = file % 8;
            }

            string digits = Convert.ToString(speicher[row, column], 2).PadLeft(8, '0').Substring(1, 7);
            string carry = Convert.ToString(speicher[0, 3], 2).PadLeft(8, '0').Substring(7, 1);
            string newValue = digits + carry;
            int fileValue = int.Parse(BinaryToHex(newValue), System.Globalization.NumberStyles.HexNumber); // erste Zahl abschneiden und 0 hinzufügen

            string firstBit = Convert.ToString(speicher[row, column], 2).PadLeft(8, '0').Substring(0, 1);
            if (firstBit == "1")
            {
                speicher[0, 3] |= 0b00000001; // carry-bit gesetzt, alle anderen bleiben gleich
            }
            else if (firstBit == "0")
            {
                speicher[0, 3] &= 0b11111110; // carry-bit nicht gesetzt, alle anderen bleiben gleich
            }

            if (d == 0)
            {
                wRegister = fileValue;
            }
            else if (d == 1)
            {
                speicher[row, column] = fileValue;
            }

            programmCounter++;
            timerIncrease(1);
            updateUI();
        }

        private void RRF(string file_d)
        {
            int d = int.Parse(file_d.Substring(0, 1));
            int file = int.Parse(BinaryToHex(file_d.Substring(1, 7)), System.Globalization.NumberStyles.HexNumber);
            int row = 0, column = 0;

            if (file == 0)
            {
                row = speicher[0, 4] / 8;
                column = speicher[0, 4] % 8;
            }
            else
            {
                row = file / 8;
                column = file % 8;
            }

            string digits = Convert.ToString(speicher[row, column], 2).PadLeft(8, '0').Substring(0, 7);
            string carry = Convert.ToString(speicher[0, 3], 2).PadLeft(8, '0').Substring(7, 1);
            string newValue = carry + digits;
            int fileValue = int.Parse(BinaryToHex(newValue), System.Globalization.NumberStyles.HexNumber); // erste Zahl abschneiden und 0 hinzufügen

            string lastBit = Convert.ToString(speicher[row, column], 2).PadLeft(8, '0').Substring(7, 1);
            if (lastBit == "1")
            {
                speicher[0, 3] |= 0b00000001; // carry-bit gesetzt, alle anderen bleiben gleich
            }
            else if (lastBit == "0")
            {
                speicher[0, 3] &= 0b11111110; // carry-bit nicht gesetzt, alle anderen bleiben gleich
            }

            if (d == 0)
            {
                wRegister = fileValue;
            }
            else if (d == 1)
            {
                speicher[row, column] = fileValue;
            }

            programmCounter++;
            timerIncrease(1);
            updateUI();
        }

        private void SUBWF(string file_d)
        {
            int d = int.Parse(file_d.Substring(0, 1));
            int file = int.Parse(BinaryToHex(file_d.Substring(1, 7)), System.Globalization.NumberStyles.HexNumber);
            int row = file / 8;
            int column = file % 8;

            int fileValue = speicher[row, column];
            SubDigitCarry(wRegister, fileValue);
            int sub = (byte)(fileValue - wRegister);

            if (d == 0)
            {
                wRegister = sub;
            }
            else if (d == 1)
            {
                if ((speicher[0, 3] & 0b00100000) == 32) // wenn RP0 gesetzt Bank1
                {
                    row = row + 16;
                    if ((row == 16 && column == 0) || (row == 16 && column == 2) || (row == 16 && column == 3) || (row == 16 && column == 4) || (row == 17 && column == 2) || (row == 17 && column == 3))
                    {
                        row = row - 16;
                    }
                }
                speicher[row, column] = sub;
            }

            // wenn ergebnis kleiner 0 carry nicht gesetzt, ansonsten gesetzt
            if (fileValue < sub)
            {
                speicher[0, 3] &= 0b11111110; // carry-bit nicht gesetzt, alle anderen bleiben gleich
            }
            else
            {
                speicher[0, 3] |= 0b00000001; // carry-bit gesetzt, alle anderen bleiben gleich
            }
            speicher[16, 3] = speicher[0, 3]; // Bank0 und Bank1 enthalten beide Status

            Zero();

            if (row == 16 && column == 1)
            {
                VTcheck();
            }
            programmCounter++;
            timerIncrease(1);
            updateUI();
        }

        private void SWAPF(string file_d)
        {
            int d = int.Parse(file_d.Substring(0, 1));
            int fileValue = int.Parse(BinaryToHex(file_d.Substring(1, 7)), System.Globalization.NumberStyles.HexNumber);
            int row = 0, column = 0;

            if (fileValue == 0) // indirekte Adressierung
            {
                row = speicher[0, 4] / 8;
                column = speicher[0, 4] % 8;
            }
            else
            {
                row = fileValue / 8;
                column = fileValue % 8;
            }

            string upper = (Convert.ToString((speicher[row, column] >> 4), 2)).PadLeft(4, '0');
            string lower = (Convert.ToString((speicher[row, column] & 0x0f), 2)).PadLeft(4, '0');
            string stringvalue = lower + upper;

            int value = Convert.ToInt16(stringvalue, 2);
            if (d == 0)
            {
                wRegister = value;
            }
            else if (d == 1)
            {
                speicher[row, column] = value;
            }

            programmCounter++;
            timerIncrease(1);
            updateUI();
        }

        private void XORWF(string file_d)
        {
            int d = int.Parse(file_d.Substring(0, 1));
            int fileValue = int.Parse(BinaryToHex(file_d.Substring(1, 7)), System.Globalization.NumberStyles.HexNumber);
            int row = 0, column = 0;

            if (fileValue == 0) // indirekte Adressierung
            {
                row = speicher[0, 4] / 8;
                column = speicher[0, 4] % 8;
            }
            else
            {
                row = fileValue / 8;
                column = fileValue % 8;
            }

            int xorValue = wRegister ^ speicher[row, column]; // verxorung der beiden werte

            if (d == 0)
            {
                wRegister = xorValue;
            }
            else if (d == 1)
            {
                if ((speicher[0, 3] & 0b00100000) == 32) // wenn RP0 gesetzt Bank1
                {
                    row = row + 16;
                    if ((row == 16 && column == 0) || (row == 16 && column == 2) || (row == 16 && column == 3) || (row == 16 && column == 4) || (row == 17 && column == 2) || (row == 17 && column == 3))
                    {
                        row = row - 16;
                    }
                }
                speicher[row, column] = xorValue;
            }

            if (row == 16 && column == 1)
            {
                VTcheck();
            }
            Zero();
            programmCounter++;
            timerIncrease(1);
            updateUI();
        }

        private void BCF(string file_b)
        {
            int b = Convert.ToInt16(file_b.Substring(0, 3), 2);
            int fileValue = int.Parse(BinaryToHex(file_b.Substring(3, 7)), System.Globalization.NumberStyles.HexNumber);
            int row = 0, column = 0;

            if (fileValue == 0) // indirekte Adressierung
            {
                row = speicher[0, 4] / 8;
                column = speicher[0, 4] % 8;
            }
            else
            {
                row = fileValue / 8;
                column = fileValue % 8;
            }

            string speicherstring = Convert.ToString(speicher[row, column], 2).PadLeft(8, '0');
            string upper = speicherstring.Substring(0, 7 - b);
            string lower = speicherstring.Substring(8 - b, b);
            string speicherneu = upper + "0" + lower;
            if ((speicher[0, 3] & 0b00100000) == 32) // wenn RP0 gesetzt Bank1
            {
                row = row + 16;
                if ((row == 16 && column == 0) || (row == 16 && column == 2) || (row == 16 && column == 3) || (row == 16 && column == 4) || (row == 17 && column == 2) || (row == 17 && column == 3))
                {
                    row = row - 16;
                }

            }

            speicher[row, column] = Convert.ToInt16(speicherneu, 2);
            if (row == 16 && column == 1)
            {
                VTcheck();
            }
            programmCounter++;
            timerIncrease(1);
            updateUI();
        }

        private void BSF(string file_b)
        {
            int b = Convert.ToInt16(file_b.Substring(0, 3), 2);
            int fileValue = int.Parse(BinaryToHex(file_b.Substring(3, 7)), System.Globalization.NumberStyles.HexNumber);
            int row = 0, column = 0;

            if (fileValue == 0) // indirekte Adressierung
            {
                row = speicher[0, 4] / 8;
                column = speicher[0, 4] % 8;
            }
            else
            {
                row = fileValue / 8;
                column = fileValue % 8;
            }

            string speicherstring = Convert.ToString(speicher[row, column], 2).PadLeft(8, '0');
            string upper = speicherstring.Substring(0, 7 - b);
            string lower = speicherstring.Substring(8 - b, b);
            string speicherneu = upper + "1" + lower;
            if ((speicher[0, 3] & 0b00100000) == 32)
            {
                row = row + 16;
                if ((row == 16 && column == 0) || (row == 16 && column == 2) || (row == 16 && column == 3) || (row == 16 && column == 4) || (row == 17 && column == 2) || (row == 17 && column == 3))
                {
                    row = row - 16;
                }
            }
            speicher[row, column] = Convert.ToInt16(speicherneu, 2);

            if (row == 16 && column == 1)
            {
                VTcheck();
            }
            programmCounter++;
            timerIncrease(1);
            updateUI();
        }

        private void BTFSC(string file_b)
        {
            int b = Convert.ToInt16(file_b.Substring(0, 3), 2);
            int fileValue = int.Parse(BinaryToHex(file_b.Substring(3, 7)), System.Globalization.NumberStyles.HexNumber);
            int row = 0, column = 0;

            if (fileValue == 0) // indirekte Adressierung
            {
                row = speicher[0, 4] / 8;
                column = speicher[0, 4] % 8;
            }
            else
            {
                row = fileValue / 8;
                column = fileValue % 8;
            }

            if ((speicher[0, 3] & 0b00100000) == 32) // wenn RP0 gesetzt Bank1
            {
                row = row + 16;
                if ((row == 16 && column == 0) || (row == 16 && column == 2) || (row == 16 && column == 3) || (row == 16 && column == 4) || (row == 17 && column == 2) || (row == 17 && column == 3))
                {
                    row = row - 16;
                }
            }
            string speicherstring = Convert.ToString(speicher[row, column], 2).PadLeft(8, '0');
            string bBit = speicherstring.Substring(7 - b, 1);

            if (bBit == "0")
            {
                NOP();
            }

            if (row == 16 && column == 1)
            {
                VTcheck();
            }
            programmCounter++;
            timerIncrease(1);
            updateUI();
        }

        private void BTFSS(string file_b)
        {
            int b = Convert.ToInt16(file_b.Substring(0, 3), 2);
            int fileValue = int.Parse(BinaryToHex(file_b.Substring(3, 7)), System.Globalization.NumberStyles.HexNumber);
            int row = 0, column = 0;

            if (fileValue == 0) // indirekte Adressierung
            {
                row = speicher[0, 4] / 8;
                column = speicher[0, 4] % 8;
            }
            else
            {
                row = fileValue / 8;
                column = fileValue % 8;
            }

            if ((speicher[0, 3] & 0b00100000) == 32) // wenn RP0 gesetzt Bank1
            {
                row = row + 16;
                if ((row == 16 && column == 0) || (row == 16 && column == 2) || (row == 16 && column == 3) || (row == 16 && column == 4) || (row == 17 && column == 2) || (row == 17 && column == 3))
                {
                    row = row - 16;
                }
            }
            string speicherstring = Convert.ToString(speicher[row, column], 2).PadLeft(8, '0');
            string bBit = speicherstring.Substring(7 - b, 1);

            if (bBit == "1")
            {
                NOP();
            }

            if (row == 16 && column == 1)
            {
                VTcheck();
            }
            programmCounter++;
            timerIncrease(1);
            updateUI();
        }

        private void ANDLW(string literal)
        {
            int andValue = int.Parse(BinaryToHex(literal), System.Globalization.NumberStyles.HexNumber);
            wRegister &= andValue; // verundung der beiden werte wird in wreg geschrieben
            Zero();
            programmCounter++;
            timerIncrease(1);
            updateUI();
        }

        private void CALL(string lowerbits)
        {
            pushStack(programmCounter + 1);
            string upperbits = HexToBinary(Convert.ToString(speicher[1, 2], 16)).Substring(3, 2);
            string programmvalue = upperbits + lowerbits;
            programmvalue = BinaryToHex(programmvalue);
            programmCounter = Convert.ToInt16(programmvalue, 16);
            timerIncrease(2);
            updateUI();
        }

        private void CLRWDT()
        {
            timerIncrease(1);
            watchdog = 0;
            speicher[16, 1] &= 0b1111_1000; // Vorteiler mit 0 beschreiben
            maxVT = 0;
            vorteiler = 0;
            speicher[0, 3] |= 0b00011000;
            speicher[16, 3] = speicher[0, 3];

            programmCounter++;
            updateUI();
        }

        private void ADDLW(string literal)
        {
            int summand = int.Parse(BinaryToHex(literal), System.Globalization.NumberStyles.HexNumber); // literal zu hexint konvertieren
            AddDigitCarry(wRegister, summand);
            wRegister += summand;
            Carry(); // wenn carry gesetzt wregister -256 -> in Carry()
            Zero();
            programmCounter++;
            timerIncrease(1);
            updateUI();
        }

        private void GOTO(string lowerbits)
        {
            string upperbits = HexToBinary(Convert.ToString(speicher[1, 2], 16)).Substring(3, 2);
            string programmvalue = upperbits + lowerbits;
            programmvalue = BinaryToHex(programmvalue);
            programmCounter = Convert.ToInt16(programmvalue, 16);
            timerIncrease(2);
            updateUI();
        }

        private void IORLW(string literal)
        {
            int orValue = int.Parse(BinaryToHex(literal), System.Globalization.NumberStyles.HexNumber);
            wRegister |= orValue; // veroderung der beiden werte wird in wreg geschrieben
            Zero();
            programmCounter++;
            timerIncrease(1);
            updateUI();
        }

        private void MOVLW(string literal)
        {
            wRegister = int.Parse(BinaryToHex(literal), System.Globalization.NumberStyles.HexNumber);
            programmCounter++;
            timerIncrease(1);
            updateUI();
        }

        private void RETFIE()
        {
            //zurückkehren zum "eigentlichen" Code
            int intnumber = popStack();
            string hexstring = intnumber.ToString("X");
            programmCounter = int.Parse(hexstring, System.Globalization.NumberStyles.HexNumber);

            //setzen des GIE-Bits (INTCON Bit 7), um weitere Interrupts zu ermöglichen
            speicher[1, 3] |= 0x80;

            timerIncrease(2);
            updateUI();
        }

        private void RETLW(string literal)
        {
            wRegister = int.Parse(BinaryToHex(literal), System.Globalization.NumberStyles.HexNumber); // MOVL
            int intnumber = popStack(); // RETURN
            string hexstring = intnumber.ToString("X");
            programmCounter = int.Parse(hexstring, System.Globalization.NumberStyles.HexNumber);
            timerIncrease(4); // 4 oder 2 cycles?
            updateUI();
        }

        private void RETURN()
        {
            int intnumber = popStack();
            string hexstring = intnumber.ToString("X");
            programmCounter = int.Parse(hexstring, System.Globalization.NumberStyles.HexNumber);
            timerIncrease(2);
            updateUI();
        }

        private void SLEEP()
        {
            speicher[0, 3] &= 0b11110111; // !PD nicht setzen
            speicher[0, 3] |= 0b00010000; // !T0 setzen
            speicher[16, 3] = speicher[0, 3];

            if (!sleepOn)
            {
                sleepOn = true;
                watchdog = 0;
            }

            timerIncrease(1);
            updateUI();
        }

        private void SUBLW(string literal)
        {
            int subtrahend = int.Parse(BinaryToHex(literal), System.Globalization.NumberStyles.HexNumber); // literal zu hexint konvertieren
            SubDigitCarry(wRegister, subtrahend);
            wRegister = subtrahend - wRegister;

            // wenn ergebnis kleiner 0 carry gesetzt, ansonsten nicht gesetzt
            if (wRegister < 0x00)
            {
                speicher[0, 3] &= 0b11111110; // carry-bit nicht gesetzt, alle anderen bleiben gleich
                wRegister = (byte)wRegister; // wRegister auf das hintere Byte kürzen
            }
            else
            {
                speicher[0, 3] |= 0b00000001; // carry-bit gesetzt, alle anderen bleiben gleich
                wRegister = (byte)wRegister; // wRegister auf das hintere Byte kürzen
            }
            speicher[16, 3] = speicher[0, 3]; // Bank0 und Bank1 enthalten beide Status

            Zero();
            programmCounter++;
            timerIncrease(1);
            updateUI();
        }

        private void XORLW(string literal)
        {
            int xorValue = int.Parse(BinaryToHex(literal), System.Globalization.NumberStyles.HexNumber);
            wRegister ^= xorValue; // verxorung der beiden werte wird in wreg geschrieben
            Zero();
            programmCounter++;
            timerIncrease(1);
            updateUI();
        }

        private void Carry()
        {
            if (wRegister > 0xff)
            {
                speicher[0, 3] |= 0b00000001; // carry-bit gesetzt, alle anderen bleiben gleich
                wRegister -= 0x100; // wregister -256 wenn carry gesetzt, da eigentlich zweistellig (8 bit)
            }
            else
            {
                speicher[0, 3] &= 0b11111110; // carry-bit nicht gesetzt, alle anderen bleiben gleich
            }
            speicher[16, 3] = speicher[0, 3]; // Bank0 und Bank1 enthalten beide Status
            updateUI();
        }

        private void AddDigitCarry(int wRegister, int zahl2)
        {
            // Substring von den letzten vier Bits
            string wRegister1 = Convert.ToString(wRegister, 16);
            string Register1 = wRegister1.Substring(wRegister1.Length - 1);
            string string2 = Convert.ToString(zahl2, 16);
            string substring2 = string2.Substring(string2.Length - 1);
            // Test wie bei carry, nur dass kein overflow existieren kann
            int dctest = Convert.ToInt16(Register1, 16) + Convert.ToInt16(substring2, 16);

            if (dctest > 0xf)
            {
                speicher[0, 3] |= 0b00000010; // dc gesetzt
            }
            else
            {
                speicher[0, 3] &= 0b11111101; // dc nicht gesetzt
            }
            speicher[16, 3] = speicher[0, 3]; // Bank0 und Bank1 enthalten beide Status
            updateUI();
        }

        private void SubDigitCarry(int wRegister, int zahl2)
        {
            // Substring von den letzten vier Bits
            string wRegister1 = Convert.ToString(wRegister, 16);
            string subwRegister1 = wRegister1.Substring(wRegister1.Length - 1);
            string string2 = Convert.ToString(zahl2, 16);
            string substring2 = string2.Substring(string2.Length - 1);
            // Test wie bei carry, nur dass kein overflow existieren kann
            int dctest = Convert.ToInt16(substring2, 16) - Convert.ToInt16(subwRegister1, 16);

            if (dctest < 0x0)
            {
                speicher[0, 3] &= 0b11111101; // dc nicht gesetzt
            }
            else
            {
                speicher[0, 3] |= 0b00000010; // dc gesetzt
            }
            speicher[16, 3] = speicher[0, 3]; // Bank0 und Bank1 enthalten beide Status
            updateUI();
        }

        private void Zero()
        {
            if (wRegister == 0x00)
            {
                speicher[0, 3] |= 0b00000100; // zero gesetzt
            }
            else
            {
                speicher[0, 3] &= 0b11111011; // zero nicht gesetzt
            }
            speicher[16, 3] = speicher[0, 3]; // Bank0 und Bank1 enthalten beide Status
            updateUI();
        }

        private void TMR0()
        {
            if ((HexToBinary(Convert.ToString(speicher[16, 1], 16))).Substring(2, 1) == "0") // T0CS-Bit
            {
                if ((HexToBinary(Convert.ToString(speicher[16, 1], 16))).Substring(4, 1) == "1") // PSA-Bit
                {
                    speicher[0, 1]++;
                }
                else
                {
                    vorteiler++;

                    if (vorteiler == maxVT)    //Prüfung, ob Wert von TMR0 nun erhöht werden darf
                    {
                        vorteiler = 0;
                        speicher[0, 1]++;
                        maxVT = setVT();
                    }

                    if (speicher[0, 1] > 0xff)
                    {
                        speicher[1, 3] |= 0b00000100;
                        speicher[0, 1] = 0x00;
                        T0_interrupt = true;
                    }
                }
            }
            updateUI();
        }

        private void WD()
        {
            if (watchdog > 18000) // Watchdog overflow
            {
                watchdog = 0;
                if ((speicher[16, 1] &= 0b0000_1000) == 8)
                {
                    vorteiler++;
                    if (vorteiler == maxVT)
                    {
                        vorteiler = 0;
                        maxVT = setVT();
                        MessageBox.Show("Watchdog ausgelöst!");
                        onReset(2);
                        run = false;
                        runButton.Text = "Run";
                        programmCounter = 0x00;
                    }
                }
                else
                {
                    MessageBox.Show("Watchdog ausgelöst!");
                    onReset(2);
                    run = false;
                    runButton.Text = "Run";
                    programmCounter = 0x00;
                }
            }
        }

        private int setVT()
        {
            string value = (HexToBinary(Convert.ToString(speicher[16, 1], 16))).Substring(5, 3);
            if ((HexToBinary(Convert.ToString(speicher[16, 1], 16))).Substring(4, 1) == "0") // PSA 0 für TMR0

                switch (value)
                {
                    case "000":
                        return 2;
                    case "001":
                        return 4;
                    case "010":
                        return 8;
                    case "011":
                        return 16;
                    case "100":
                        return 32;
                    case "101":
                        return 64;
                    case "110":
                        return 128;
                    case "111":
                        return 256;
                    default:
                        MessageBox.Show("Vorteiler Fehler");
                        return 0;
                }
            else // PSA 1 für Watchdog
            {
                switch (value)
                {
                    case "000":
                        return 1;
                    case "001":
                        return 2;
                    case "010":
                        return 4;
                    case "011":
                        return 8;
                    case "100":
                        return 16;
                    case "101":
                        return 32;
                    case "110":
                        return 64;
                    case "111":
                        return 128;
                    default:
                        MessageBox.Show("Vorteiler Fehler");
                        return 0;
                }
            }
        }

        private void VTcheck()
        {
            string newVT = (HexToBinary(Convert.ToString(speicher[16, 1], 16))).Substring(5, 3);
            if (Convert.ToString(maxVT) != newVT)
            {
                maxVT = setVT();

            }
        }

        private void checkBox1_CheckedChanged(object sender, EventArgs e)
        {
            WDTE = WDTECheckbox.Checked;
            updateUI();
        }

        private void quarzFreq_comboBox_SelectedIndexChanged(object sender, EventArgs e)
        {
            switch (quarzFreq_comboBox.SelectedIndex)
            {
                case 0: //1 kHz = 0,001 MHz
                    quarzFreq = 0.001f;
                    break;
                case 1: //25 kHz = 0,025 MHz
                    quarzFreq = 0.025f;
                    break;
                case 2: //100 kHz = 0,1 MHz
                    quarzFreq = 0.1f;
                    break;
                case 3: //200 kHz = 0,2 MHz
                    quarzFreq = 0.2f;
                    break;
                case 4: //455 kHz = 0,455 MHz
                    quarzFreq = 0.455f;
                    break;
                case 5: //2 MHz
                    quarzFreq = 2;
                    break;
                case 6: //4 MHz
                    quarzFreq = 4;
                    break;
                case 7: //10 MHz
                    quarzFreq = 10;
                    break;

                default:
                    MessageBox.Show("Bitte einen gültigen Wert auswählen.");
                    break;
            }
        }

        private void timerIncrease(int cycle)
        {
            float runtime = 4 / quarzFreq;    //"one instruction cycle consists of four oscillator periods", T=1/f. runtime: Zeit pro Cycle in µs
            float add = (float)Math.Round(cycle * runtime, 1);
            if (WDTE)
            {
                watchdog += add;
            }
            laufzeitzaehler += add;
            while (cycle > 0)
            {
                if (!sleepOn)
                {
                    TMR0();
                }
                cycle--;
            }

            InterruptMaker();   //um es nicht bei jedem Command einfügen zu müssen
            WD();
        }

        private async void nextStep(bool loop)
        {
            if (loop)
            {
                while (run)
                {
                    if (Convert.ToBoolean(code_dataGridView.Rows[zeile].Cells[0].Value) && !skip)   //Breakpoint
                    {
                        run = false;
                        runButton.Text = "Run";
                        skip = true;
                    }
                    else
                    {
                        skip = false;
                        if (fastRun)     // bei reset-button auch anpassen
                        {
                            await Task.Delay(1);
                        }
                        else
                        {
                            await Task.Delay(500);
                        }
                        removeHighlightLine();

                        //"echter" Programmaufruf
                        identifyCommand(executableLines[programmCounter]);
                        highlightLine();
                    }
                }
            }
            else
            {
                removeHighlightLine();

                //"echter" Programmaufruf
                identifyCommand(executableLines[programmCounter]);
                highlightLine();
            }
        }

        private void StepButton_Click(object sender, EventArgs e)
        {
            nextStep(false);
        }

        private void RunButton_Click(object sender, EventArgs e)
        {
            if (run)
            {
                run = false;
                runButton.Text = "Run";
            }
            else
            {
                run = true;
                runButton.Text = "Pause";
                nextStep(true);
            }
        }

        private void ResetButton_Click(object sender, EventArgs e)
        {
            resetFile();
        }

        private async void resetFile()
        {
            run = false;
            runButton.Text = "Run";
            if (fastRun)
            {
                await Task.Delay(50);
            }
            else
            {
                await Task.Delay(505);
            }
            onReset(0);
            removeHighlightLine();
            programmCounter = 0x00;
            highlightLine();
            laufzeitzaehler = 0;
            watchdog = 0;
            wRegister = 0x00;
            vorteiler = 0x00;
            maxVT = 0xff;
            WDTE = true;
            stack1[0] = null;
            stack1[1] = null;
            stack1[2] = null;
            stack1[3] = null;
            stack1[4] = null;
            stack1[5] = null;
            stack1[6] = null;
            stack1[7] = null;
            stackPointer = 0;
            sleepOn = false;
            updateUI();
        }

        private void ShowStatus()
        {
            string status = HexToBinary(Convert.ToString(speicher[0, 3], 16));
            status_label.Text = "Status: " + speicher[0, 3].ToString("X2");

            IRP_button.Text = status.Substring(0, 1);

            RP1_button.Text = status.Substring(1, 1);

            RP0_button.Text = status.Substring(2, 1);

            NotTO_button.Text = status.Substring(3, 1);

            NotPD_button.Text = status.Substring(4, 1);

            Z_button.Text = status.Substring(5, 1);

            DC_button.Text = status.Substring(6, 1);

            C_button.Text = status.Substring(7, 1);
        }

        private void ShowOption()
        {
            string option = HexToBinary(Convert.ToString(speicher[16, 1], 16));
            Option_label.Text = "Option: " + speicher[16, 1].ToString("X2");

            NotRBPU_button.Text = option.Substring(0, 1);

            INTEDG_button.Text = option.Substring(1, 1);

            T0CS_button.Text = option.Substring(2, 1);

            T0SE_button.Text = option.Substring(3, 1);

            PSA_button.Text = option.Substring(4, 1);

            PS2_button.Text = option.Substring(5, 1);

            PS1_button.Text = option.Substring(6, 1);

            PS0_button.Text = option.Substring(7, 1);
        }

        private void ShowINTCON()
        {
            string intcon = HexToBinary(Convert.ToString(speicher[1, 3], 16));
            Intcon_label.Text = "INTCON: " + speicher[1, 3].ToString("X2");

            GIE_button.Text = intcon.Substring(0, 1);

            EEIE_button.Text = intcon.Substring(1, 1);

            T0IE_button.Text = intcon.Substring(2, 1);

            INTE_button.Text = intcon.Substring(3, 1);

            RBIE_button.Text = intcon.Substring(4, 1);

            T0IF_button.Text = intcon.Substring(5, 1);

            INTF_button.Text = intcon.Substring(6, 1);

            RBIF_button.Text = intcon.Substring(7, 1);
        }

        private void showPC()
        {
            speicher[0, 2] = (byte)(programmCounter & 0xFF);
            speicher[16, 2] = speicher[0, 2];

        }

        private void fastRun_checkBox_CheckedChanged(object sender, EventArgs e)
        {
            fastRun = fastRun_checkBox.Checked;
        }

        private void PinA7_button_Click(object sender, EventArgs e)
        {
            if ((speicher[0, 5] & 0x80) != 128) //Prüfung, ob es NICHT auf 1 steht
            {
                PinA7_button.Text = "1";
                speicher[0, 5] |= 0x80; //1
            }
            else
            {
                PinA7_button.Text = "0";
                speicher[0, 5] &= 0x7F; //0
            }
            updateUI();
        }

        private void PinA6_button_Click(object sender, EventArgs e)
        {
            if ((speicher[0, 5] & 0x40) != 64) //Prüfung, ob es NICHT auf 1 steht
            {
                PinA6_button.Text = "1";
                speicher[0, 5] |= 0x40; //1
            }
            else
            {
                PinA6_button.Text = "0";
                speicher[0, 5] &= 0xBF; //0
            }
            updateUI();
        }

        private void PinA5_button_Click(object sender, EventArgs e)
        {
            if ((speicher[0, 5] & 0x20) != 32) //Prüfung, ob es NICHT auf 1 steht
            {
                PinA5_button.Text = "1";
                speicher[0, 5] |= 0x20; //1
            }
            else
            {
                PinA5_button.Text = "0";
                speicher[0, 5] &= 0xDF; //0
            }
            updateUI();
        }

        private void PinA4_button_Click(object sender, EventArgs e)
        {
            if ((speicher[0, 5] & 0x10) != 16) //Prüfung, ob es NICHT auf 1 steht
            {
                PinA4_button.Text = "1";
                speicher[0, 5] |= 0x10; //1
            }
            else
            {
                PinA4_button.Text = "0";
                speicher[0, 5] &= 0xEF; //0
            }
            if ((HexToBinary(Convert.ToString(speicher[16, 1], 16))).Substring(2, 1) == "1") // T0CS-Bit
            {
                if ((HexToBinary(Convert.ToString(speicher[16, 1], 16))).Substring(3, 1) == "0") // T0SE
                {
                    if ((HexToBinary(Convert.ToString(speicher[0, 5], 16))).Substring(3, 1) == "0") // RA4
                    {
                        if ((HexToBinary(Convert.ToString(speicher[16, 1], 16))).Substring(4, 1) == "1") // PSA-Bit
                        {
                            speicher[0, 1]++;
                        }
                        else
                        {
                            vorteiler++;

                            if (vorteiler == maxVT)    //Prüfung, ob Wert von TMR0 nun erhöht werden darf
                            {
                                vorteiler = 0;
                                speicher[0, 1]++;
                                maxVT = setVT();
                            }

                            if (speicher[0, 1] > 0xff)
                            {
                                speicher[1, 3] |= 0b00000100;
                                speicher[0, 1] = 0x00;
                                T0_interrupt = true;
                            }
                        }
                    }
                }
                else
                {
                    if ((HexToBinary(Convert.ToString(speicher[0, 5], 16))).Substring(3, 1) == "1") // RA4
                    {
                        if ((HexToBinary(Convert.ToString(speicher[16, 1], 16))).Substring(4, 1) == "1") // PSA-Bit
                        {
                            speicher[0, 1]++;
                        }
                        else
                        {
                            vorteiler++;

                            if (vorteiler == maxVT)    //Prüfung, ob Wert von TMR0 nun erhöht werden darf
                            {
                                vorteiler = 0;
                                speicher[0, 1]++;
                                maxVT = setVT();
                            }

                            if (speicher[0, 1] > 0xff)
                            {
                                speicher[1, 3] |= 0b00000100;
                                speicher[0, 1] = 0x00;
                                T0_interrupt = true;
                            }
                        }
                    }
                }
            }
            updateUI();

        }

        private void PinA3_button_Click(object sender, EventArgs e)
        {
            if ((speicher[0, 5] & 0x8) != 8) //Prüfung, ob es NICHT auf 1 steht
            {
                PinA3_button.Text = "1";
                speicher[0, 5] |= 0x8; //1
            }
            else
            {
                PinA3_button.Text = "0";
                speicher[0, 5] &= 0xF7; //0
            }
            updateUI();
        }

        private void PinA2_button_Click(object sender, EventArgs e)
        {
            if ((speicher[0, 5] & 0x4) != 4) //Prüfung, ob es NICHT auf 1 steht
            {
                PinA2_button.Text = "1";
                speicher[0, 5] |= 0x4; //1
            }
            else
            {
                PinA2_button.Text = "0";
                speicher[0, 5] &= 0xFB; //0
            }
            updateUI();
        }

        private void PinA1_button_Click(object sender, EventArgs e)
        {
            if ((speicher[0, 5] & 0x2) != 2) //Prüfung, ob es NICHT auf 1 steht
            {
                PinA1_button.Text = "1";
                speicher[0, 5] |= 0x2; //1
            }
            else
            {
                PinA1_button.Text = "0";
                speicher[0, 5] &= 0xFD; //0
            }
            updateUI();
        }

        private void PinA0_button_Click(object sender, EventArgs e)
        {
            if ((speicher[0, 5] & 0x1) != 1) //Prüfung, ob es NICHT auf 1 steht
            {
                PinA0_button.Text = "1";
                speicher[0, 5] |= 0x1; //1
            }
            else
            {
                PinA0_button.Text = "0";
                speicher[0, 5] &= 0xFE; //0
            }
            updateUI();
        }

        private void PinB7_button_Click(object sender, EventArgs e)
        {
            if ((speicher[0, 6] & 0x80) != 128) //Prüfung, ob es NICHT auf 1 steht
            {
                PinB7_button.Text = "1";
                speicher[0, 6] |= 0x80; //1
            }
            else
            {
                PinB7_button.Text = "0";
                speicher[0, 6] &= 0x7F; //0
            }

            if ((speicher[16, 6] & 0x80) == 128) //Pin gerade Input?!
            {
                RB4_7_changed = true;
            }

            updateUI();
        }

        private void PinB6_button_Click(object sender, EventArgs e)
        {
            if ((speicher[0, 6] & 0x40) != 64) //Prüfung, ob es NICHT auf 1 steht
            {
                PinB6_button.Text = "1";
                speicher[0, 6] |= 0x40; //1
            }
            else
            {
                PinB6_button.Text = "0";
                speicher[0, 6] &= 0xBF; //0
            }

            if ((speicher[16, 6] & 0x40) == 64) //Pin gerade Input?!
            {
                RB4_7_changed = true;
            }

            updateUI();
        }

        private void PinB5_button_Click(object sender, EventArgs e)
        {
            if ((speicher[0, 6] & 0x20) != 32) //Prüfung, ob es NICHT auf 1 steht
            {
                PinB5_button.Text = "1";
                speicher[0, 6] |= 0x20; //1
            }
            else
            {
                PinB5_button.Text = "0";
                speicher[0, 6] &= 0xDF; //0
            }

            if ((speicher[16, 6] & 0x20) == 32) //Pin gerade Input?!
            {
                RB4_7_changed = true;
            }

            updateUI();
        }

        private void PinB4_button_Click(object sender, EventArgs e)
        {
            if ((speicher[0, 6] & 0x10) != 16) //Prüfung, ob es NICHT auf 1 steht
            {
                PinB4_button.Text = "1";
                speicher[0, 6] |= 0x10; //1
            }
            else
            {
                PinB4_button.Text = "0";
                speicher[0, 6] &= 0xEF; //0
            }

            if ((speicher[16, 6] & 0x10) == 16) //Pin gerade Input?!
            {
                RB4_7_changed = true;
            }

            updateUI();
        }

        private void PinB3_button_Click(object sender, EventArgs e)
        {
            if ((speicher[0, 6] & 0x8) != 8) //Prüfung, ob es NICHT auf 1 steht
            {
                PinB3_button.Text = "1";
                speicher[0, 6] |= 0x8; //1
            }
            else
            {
                PinB3_button.Text = "0";
                speicher[0, 6] &= 0xF7; //0
            }
            updateUI();
        }

        private void PinB2_button_Click(object sender, EventArgs e)
        {
            if ((speicher[0, 6] & 0x4) != 4) //Prüfung, ob es NICHT auf 1 steht
            {
                PinB2_button.Text = "1";
                speicher[0, 6] |= 0x4; //1
            }
            else
            {
                PinB2_button.Text = "0";
                speicher[0, 6] &= 0xFB; //0
            }
            updateUI();
        }

        private void PinB1_button_Click(object sender, EventArgs e)
        {
            if ((speicher[0, 6] & 0x2) != 2) //Prüfung, ob es NICHT auf 1 steht
            {
                PinB1_button.Text = "1";
                speicher[0, 6] |= 0x2; //1
            }
            else
            {
                PinB1_button.Text = "0";
                speicher[0, 6] &= 0xFD; //0
            }
            updateUI();
        }

        private void PinB0_button_Click(object sender, EventArgs e)
        {
            if ((speicher[0, 6] & 0x1) != 1) //Prüfung, ob es NICHT auf 1 steht => steigende Taktflanke
            {
                PinB0_button.Text = "1";

                speicher[0, 6] |= 0x1; // RB0-Bit auf 1
                if ((speicher[16, 1] & 0x40) == 64)  //Prüfung, ob "rising edge" (steigende Taktflanke) im Option Pin 6 (INTEDG) gesetzt ist
                {
                    RB0_flanke = true;
                }
            }
            else
            {
                PinB0_button.Text = "0";

                speicher[0, 6] &= 0xFE; // RB0-Bit auf 0 => fallende Taktflanke
                if ((speicher[16, 1] & 0x40) == 0)   //Prüfung, ob "falling edge" (fallende Taktflanke) im Option Pin 6 (INTEDG) gesetzt ist
                {
                    RB0_flanke = true;
                }
            }
            updateUI();
        }


        private void IRP_button_Click(object sender, EventArgs e)   //7. Bit
        {
            if ((speicher[0, 3] & 0x80) != 128) //Prüfung, ob es NICHT auf 1 steht
            {
                speicher[0, 3] |= 0x80; //1
            }
            else
            {
                speicher[0, 3] &= 0x7F; //0
            }
            updateUI();
        }

        private void RP1_button_Click(object sender, EventArgs e)   //6. Bit
        {
            if ((speicher[0, 3] & 0x40) != 64) //Prüfung, ob es NICHT auf 1 steht
            {
                speicher[0, 3] |= 0x40; //1
            }
            else
            {
                speicher[0, 3] &= 0xBF; //0
            }
            updateUI();
        }

        private void RP0_button_Click(object sender, EventArgs e)   //5. Bit
        {
            if ((speicher[0, 3] & 0x20) != 32) //Prüfung, ob es NICHT auf 1 steht
            {
                speicher[0, 3] |= 0x20; //1
            }
            else
            {
                speicher[0, 3] &= 0xDF; //0
            }
            updateUI();
        }

        private void NotTO_button_Click(object sender, EventArgs e) //4. Bit
        {
            if ((speicher[0, 3] & 0x10) != 16) //Prüfung, ob es NICHT auf 1 steht
            {
                speicher[0, 3] |= 0x10; //1
            }
            else
            {
                speicher[0, 3] &= 0xEF; //0
            }
            updateUI();
        }

        private void NotPD_button_Click(object sender, EventArgs e) //3. Bit
        {
            if ((speicher[0, 3] & 0x8) != 8) //Prüfung, ob es NICHT auf 1 steht
            {
                speicher[0, 3] |= 0x8; //1
            }
            else
            {
                speicher[0, 3] &= 0xF7; //0
            }
            updateUI();
        }

        private void Z_button_Click(object sender, EventArgs e) //2. Bit
        {
            if ((speicher[0, 3] & 0x4) != 4) //Prüfung, ob es NICHT auf 1 steht
            {
                speicher[0, 3] |= 0x4; //1
            }
            else
            {
                speicher[0, 3] &= 0xFB; //0
            }
            updateUI();
        }

        private void DC_button_Click(object sender, EventArgs e)    //1. Bit
        {
            if ((speicher[0, 3] & 0x2) != 2) //Prüfung, ob es NICHT auf 1 steht
            {
                speicher[0, 3] |= 0x2; //1
            }
            else
            {
                speicher[0, 3] &= 0xFD; //0
            }
            updateUI();
        }

        private void C_button_Click(object sender, EventArgs e) //0. Bit
        {
            if ((speicher[0, 3] & 0x1) != 1) //Prüfung, ob es NICHT auf 1 steht
            {
                speicher[0, 3] |= 0x1; //1
            }
            else
            {
                speicher[0, 3] &= 0xFE; //0
            }
            updateUI();
        }

        private void NotRBPU_button_Click(object sender, EventArgs e)   //7. Bit
        {
            if ((speicher[16, 1] & 0x80) != 128) //Prüfung, ob es NICHT auf 1 steht
            {
                speicher[16, 1] |= 0x80; //1
            }
            else
            {
                speicher[16, 1] &= 0x7F; //0
            }
            updateUI();
        }

        private void INTEDG_button_Click(object sender, EventArgs e)    //6. bit
        {
            if ((speicher[16, 1] & 0x40) != 64) //Prüfung, ob es NICHT auf 1 steht
            {
                speicher[16, 1] |= 0x40; //1
            }
            else
            {
                speicher[16, 1] &= 0xBF; //0
            }
            updateUI();
        }

        private void T0CS_button_Click(object sender, EventArgs e)  //5. Bit
        {
            if ((speicher[16, 1] & 0x20) != 32) //Prüfung, ob es NICHT auf 1 steht
            {
                speicher[16, 1] |= 0x20; //1
            }
            else
            {
                speicher[16, 1] &= 0xDF; //0
            }
            updateUI();
        }

        private void T0SE_button_Click(object sender, EventArgs e)  //4. Bit
        {
            if ((speicher[16, 1] & 0x10) != 16) //Prüfung, ob es NICHT auf 1 steht
            {
                speicher[16, 1] |= 0x10; //1
            }
            else
            {
                speicher[16, 1] &= 0xEF; //0
            }
            updateUI();
        }

        private void PSA_button_Click(object sender, EventArgs e)   //3. Bit
        {
            if ((speicher[16, 1] & 0x8) != 8) //Prüfung, ob es NICHT auf 1 steht
            {
                speicher[16, 1] |= 0x8; //1
            }
            else
            {
                speicher[16, 1] &= 0xF7; //0
            }
            updateUI();
        }

        private void PS2_button_Click(object sender, EventArgs e)   //2. Bit
        {
            if ((speicher[16, 1] & 0x4) != 4) //Prüfung, ob es NICHT auf 1 steht
            {
                speicher[16, 1] |= 0x4; //1
            }
            else
            {
                speicher[16, 1] &= 0xFB; //0
            }
            updateUI();
        }

        private void PS1_button_Click(object sender, EventArgs e)   //1. Bit
        {
            if ((speicher[16, 1] & 0x2) != 2) //Prüfung, ob es NICHT auf 1 steht
            {
                speicher[16, 1] |= 0x2; //1
            }
            else
            {
                speicher[16, 1] &= 0xFD; //0
            }
            updateUI();
        }

        private void PS0_button_Click(object sender, EventArgs e)   //0. Bit
        {
            if ((speicher[16, 1] & 0x1) != 1) //Prüfung, ob es NICHT auf 1 steht
            {
                speicher[16, 1] |= 0x1; //1
            }
            else
            {
                speicher[16, 1] &= 0xFE; //0
            }
            updateUI();
        }

        private void GIE_button_Click(object sender, EventArgs e)   //7. Bit
        {
            if ((speicher[1, 3] & 0x80) != 128) //Prüfung, ob es NICHT auf 1 steht
            {
                speicher[1, 3] |= 0x80; //1
            }
            else
            {
                speicher[1, 3] &= 0x7F; //0
            }
            speicher[17, 3] = speicher[1, 3];
            updateUI();
        }

        private void EEIE_button_Click(object sender, EventArgs e)  //6. Bit
        {
            if ((speicher[1, 3] & 0x40) != 64) //Prüfung, ob es NICHT auf 1 steht
            {
                speicher[1, 3] |= 0x40; //1
            }
            else
            {
                speicher[1, 3] &= 0xBF; //0
            }
            speicher[17, 3] = speicher[1, 3];
            updateUI();
        }

        private void T0IE_button_Click(object sender, EventArgs e)  //5. Bit
        {
            if ((speicher[1, 3] & 0x20) != 32) //Prüfung, ob es NICHT auf 1 steht
            {
                speicher[1, 3] |= 0x20; //1
            }
            else
            {
                speicher[1, 3] &= 0xDF; //0
            }
            speicher[17, 3] = speicher[1, 3];
            updateUI();
        }

        private void INTE_button_Click(object sender, EventArgs e)  //4. Bit
        {
            if ((speicher[1, 3] & 0x10) != 16) //Prüfung, ob es NICHT auf 1 steht
            {
                speicher[1, 3] |= 0x10; //1
            }
            else
            {
                speicher[1, 3] &= 0xEF; //0
            }
            speicher[17, 3] = speicher[1, 3];
            updateUI();
        }

        private void RBIE_button_Click(object sender, EventArgs e)  //3. Bit
        {
            if ((speicher[1, 3] & 0x8) != 8) //Prüfung, ob es NICHT auf 1 steht
            {
                speicher[1, 3] |= 0x8; //1
            }
            else
            {
                speicher[1, 3] &= 0xF7; //0
            }
            speicher[17, 3] = speicher[1, 3];
            updateUI();
        }

        private void T0IF_button_Click(object sender, EventArgs e)  //2. Bit
        {
            if ((speicher[1, 3] & 0x4) != 4) //Prüfung, ob es NICHT auf 1 steht
            {
                speicher[1, 3] |= 0x4; //1
            }
            else
            {
                speicher[1, 3] &= 0xFB; //0
            }
            speicher[17, 3] = speicher[1, 3];
            updateUI();
        }

        private void INTF_button_Click(object sender, EventArgs e)  //1. Bit
        {
            if ((speicher[1, 3] & 0x2) != 2) //Prüfung, ob es NICHT auf 1 steht
            {
                speicher[1, 3] |= 0x2; //1
            }
            else
            {
                speicher[1, 3] &= 0xFD; //0
            }
            speicher[17, 3] = speicher[1, 3];
            updateUI();
        }

        private void RBIF_button_Click(object sender, EventArgs e)  //0. Bit
        {
            if ((speicher[1, 3] & 0x1) != 1) //Prüfung, ob es NICHT auf 1 steht
            {
                speicher[1, 3] |= 0x1; //1
            }
            else
            {
                speicher[1, 3] &= 0xFE; //0
            }
            speicher[17, 3] = speicher[1, 3];
            updateUI();
        }

        private void WReg_TextBox_LostFocus(object sender, EventArgs e) //Funktion zum Speichern von einem manuell eingetragenen Wert für das W-Register
        {
            int wRegUpdated;
            string txt = WReg_TextBox.Text;

            if (txt.Length == 1 && int.TryParse(txt, System.Globalization.NumberStyles.HexNumber, null, out wRegUpdated))   //Überprüfung, ob in Hex umgewandelt werden kann. Falls ja: True und Wert wird in wRegUpdated gespeichert. ansonsten false.
            {
                wRegister = (byte)wRegUpdated;
            }
            else if (txt.Length == 2 && int.TryParse(txt, System.Globalization.NumberStyles.HexNumber, null, out wRegUpdated))
            {
                wRegister = (byte)wRegUpdated;
            }
            else
            {
                MessageBox.Show("Bitte einen gültigen Wert (0-FF) für das W-Register eingeben.");
            }

            updateUI();
        }

        private void ChangeRegister_button_Click(object sender, EventArgs e)
        {
            int adr, val;
            string adrtxt = updateAddress_textbox.Text;
            string valtxt = updateValue_textbox.Text;

            if (int.TryParse(adrtxt, System.Globalization.NumberStyles.HexNumber, null, out adr) && int.TryParse(valtxt, System.Globalization.NumberStyles.HexNumber, null, out val))   //"Ist es eine gültige Eingabe?"
            {
                setSpeicher(adr, val);

                updateAddress_textbox.Text = "";
                updateValue_textbox.Text = "";
            }
            else
            {
                MessageBox.Show("Bitte gültige Werte (0 - FF) eingeben.");
            }

            updateUI();
        }

        private void setSpeicher(int adresse, int wert)
        {
            int speicherZeile = adresse / 8;
            int speicherSpalte = adresse % 8;

            if (adresse == 2)
            {
                removeHighlightLine();
                programmCounter = wert;
                highlightLine();
            }

            speicher[speicherZeile, speicherSpalte] = wert;
        }

        private void powerOnReset_button_Click(object sender, EventArgs e)
        {
            onReset(0);
            updateUI();
        }

        private void notMCLRReset_button_Click(object sender, EventArgs e)
        {
            onReset(1);
            updateUI();
        }

        private void otherReset_button_Click(object sender, EventArgs e)
        {
            onReset(2);
            updateUI();
        }
    }
}
