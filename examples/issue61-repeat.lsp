;;; Issue #61, option B: records of a Repeat (spec_1_pos, spec_1_Name, spec_1_col, …). Fixture for a manual check in
;;; AutoCAD: import examples/issue61-repeat-schema.json with VELEC_SCHEMA_IMPORT, load this file with APPLOAD and run
;;; VELECREPEATSAMPLE. The palette shows the device with the groups:
;;;   Спецификация  2 записи — Спецификация №1 (1 · Светильник (корпус) · 1.00), Спецификация №2 (2 · Лампа · 2.00);
;;;   Соединения    0 записей.
;;; VELECREPEATDEMO calls the LISP functions of the plugin on the device:
;;;   (velec-item-add ent "conn" '(("target" . "ЩО1.1") ("cable" . "ВВГнг 3x1.5")))  → 1
;;;   (velec-item-count ent "conn")                                                 → 1
;;;   (velec-item-get ent "spec" 2)  → (("pos" . "2") ("Name" . "Лампа") ("col" . 2.0))
;;;   (velec-item-remove-last ent "conn")                                           → 1
;;; Every call is one Undo step; an error (unknown collection or field, 16 KB of XDATA) is printed and the result is nil.
;;; VELECREPEATBROKEN creates a device with a gap (no record №2) and an incomplete record №3 for VELEC_CHECK.
;;; The commands VELEC_ITEM_ADD and VELEC_ITEM_REMOVE_LAST do the same for the selected objects, e.g. the macro
;;;   ^C^C_VELEC_ITEM_ADD;_conn;ЩО1.1;;

(defun velec:repeatblock ()
  ;; The DEVICE template of the schema is for block references only.
  (if (not (tblsearch "BLOCK" "VELEC_DEVICE"))
    (progn
      (entmake '((0 . "BLOCK") (2 . "VELEC_DEVICE") (70 . 0) (10 0.0 0.0 0.0)))
      (entmake '((0 . "CIRCLE") (10 0.0 0.0 0.0) (40 . 2.0)))
      (entmake '((0 . "ENDBLK")))))
  "VELEC_DEVICE")

(defun c:VELECREPEATSAMPLE (/ device)
  (regapp "BobrovXDATA")
  (setq device
    (entmakex
      (list '(0 . "INSERT") (cons 2 (velec:repeatblock)) '(10 0.0 0.0 0.0)
        (list -3 (list "BobrovXDATA" '(1070 . 2)
                   '(1000 . "TEMPLATE") '(1000 . "DEVICE") '(1000 . "Device_Type") '(1000 . "Светильник")
                   '(1000 . "spec_1_pos") '(1000 . "1") '(1000 . "spec_1_Name") '(1000 . "Светильник (корпус)")
                   '(1000 . "spec_1_col") '(1040 . 1.0)
                   '(1000 . "spec_2_pos") '(1000 . "2") '(1000 . "spec_2_Name") '(1000 . "Лампа")
                   '(1000 . "spec_2_col") '(1040 . 2.0))))))
  (setq velec:repeatdevice device)
  (setvar "PICKFIRST" 1)
  (sssetfirst nil (ssadd device))
  (princ "\nУстройство выбрано: 2 записи «Спецификация», 0 записей «Соединение».")
  (princ))

(defun c:VELECREPEATDEMO (/ ent)
  (setq ent velec:repeatdevice)
  (if (not (and ent (entget ent)))
    (princ "\nСначала выполните VELECREPEATSAMPLE.")
    (progn
      (princ "\nДобавлено соединение №")
      (princ (velec-item-add ent "conn" '(("target" . "ЩО1.1") ("cable" . "ВВГнг 3x1.5"))))
      (princ "\nСоединений: ")
      (princ (velec-item-count ent "conn"))
      (princ "\nСпецификация №2: ")
      (prin1 (velec-item-get ent "spec" 2))
      (princ "\nУдалено соединение №")
      (princ (velec-item-remove-last ent "conn"))
      (princ "\nОшибка выводится в командную строку, результат — nil: ")
      (prin1 (velec-item-add ent "conn" '(("length" . 5.0))))))
  (princ))

(defun c:VELECREPEATBROKEN (/ device)
  ;; For VELEC_CHECK: no record №2 and record №3 without the field col.
  (regapp "BobrovXDATA")
  (setq device
    (entmakex
      (list '(0 . "INSERT") (cons 2 (velec:repeatblock)) '(10 10.0 0.0 0.0)
        (list -3 (list "BobrovXDATA" '(1070 . 2)
                   '(1000 . "TEMPLATE") '(1000 . "DEVICE")
                   '(1000 . "spec_1_pos") '(1000 . "1") '(1000 . "spec_1_Name") '(1000 . "Шкаф")
                   '(1000 . "spec_1_col") '(1040 . 1.0)
                   '(1000 . "spec_3_pos") '(1000 . "3") '(1000 . "spec_3_Name") '(1000 . "Лоток"))))))
  (princ "\nСоздано устройство с записями №1 и №3 (без col); выполните VELEC_CHECK.")
  (princ))

(princ "\nVELECREPEATSAMPLE — устройство с двумя записями спецификации; VELECREPEATDEMO — вызвать velec-item-*;")
(princ "\nVELECREPEATBROKEN — устройство с пропуском №2 и неполной записью №3 для VELEC_CHECK.")
(princ)
