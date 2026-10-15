;;; TZXDATA stage 3: XDATA BobrovXDATA v2 fixture for docs/autocad-verification.md.
;;; Import examples/schema.json with VELEC_SCHEMA_IMPORT first, load this file with APPLOAD,
;;; run VELECV2SAMPLE, then VELECXDATA to print the XDATA of the selected objects
;;; and VELECV2TOUCH to change object B outside the palette.

(defun velec:v2line (y data)
  (entmakex
    (list '(0 . "LINE") (list 10 0.0 y 0.0) (list 11 10.0 y 0.0)
      (list -3 (cons "BobrovXDATA" data)))))

(defun c:VELECV2SAMPLE (/ device a b c selection)
  (regapp "BobrovXDATA")
  (regapp "OtherApp")
  (setq device '((1000 . "DEVICE_ID") (1000 . "3F2504E0-4F89-11D3-9A0C-0305E82C3301")))
  ;; A: every type of section 6.1 and the cases of 6.5 (Old_Param is not in the schema,
  ;; Current is a string instead of 1040, ZZ is not a key of the Phase enum).
  (setq a (velec:v2line 0.0
    (append '((1070 . 2)) device
      '((1000 . "TEMPLATE") (1000 . "DEVICE")
        (1000 . "Device_Type") (1000 . "Щит")
        (1000 . "Power") (1040 . 2.5)
        (1000 . "Poles") (1071 . 3)
        (1000 . "Tray_Covered") (1070 . 1)
        (1000 . "Voltage") (1000 . "_AC_220V_50Hz")
        (1000 . "Materials") (1002 . "{")
          (1002 . "{") (1000 . "Mat_Mark") (1000 . "CD35") (1000 . "Mat_Count") (1040 . 2.0) (1002 . "}")
        (1002 . "}")
        (1000 . "Old_Param") (1000 . "старое значение")
        (1000 . "Current") (1000 . "много")
        (1000 . "Phase") (1000 . "ZZ")))))
  ;; Another RegApp after BobrovXDATA must survive every write.
  (entmod (append (entget a) '((-3 ("OtherApp" (1000 . "keep") (1071 . 42))))))
  ;; B: the same DEVICE_ID, as after COPY (TZXDATA 6.4).
  (setq b (velec:v2line 5.0 (append '((1070 . 2)) device '((1000 . "Device_Type") (1000 . "Копия")))))
  (setq velec:v2b b)
  ;; C: format v1 (no (1070 . 2) first).
  (setq c (velec:v2line 10.0 '((1000 . "Name=Объект v1"))))
  (setvar "PICKFIRST" 1)
  (setq selection (ssadd a))
  (ssadd b selection)
  (ssadd c selection)
  (sssetfirst nil selection)
  (princ "\nТри объекта выбраны: A и B в формате v2 (общий DEVICE_ID), C в формате v1.")
  (princ))

(defun c:VELECV2TOUCH (/ data)
  (if (and velec:v2b (entget velec:v2b))
    (progn
      (setq data (entget velec:v2b '("BobrovXDATA")))
      (entmod (subst '(-3 ("BobrovXDATA" (1070 . 2)
                            (1000 . "DEVICE_ID") (1000 . "3F2504E0-4F89-11D3-9A0C-0305E82C3301")
                            (1000 . "Device_Type") (1000 . "Изменено")))
                     (assoc -3 data) data))
      (princ (strcat "\nИзменён объект B " (cdr (assoc 5 (entget velec:v2b))) ".")))
    (princ "\nСначала выполните VELECV2SAMPLE."))
  (princ))

(defun c:VELECXDATA (/ selection index item)
  (if (setq selection (ssget "_I"))
    (progn
      (setq index 0)
      (while (< index (sslength selection))
        (setq item (ssname selection index))
        (princ (strcat "\n" (cdr (assoc 5 (entget item))) ": "))
        (prin1 (cdr (assoc -3 (entget item '("*")))))
        (setq index (1+ index))))
    (princ "\nВыберите объекты перед командой."))
  (princ))

(princ "\nVELECV2SAMPLE — создать объекты XDATA v2; VELECXDATA — вывести XDATA выбранных объектов; VELECV2TOUCH — изменить объект B.")
(princ)
