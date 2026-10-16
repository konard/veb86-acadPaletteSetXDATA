;;; TZXDATA stage 7: template fixture for docs/autocad-verification.md (templates on objects).
;;; Import examples/schema.json with VELEC_SCHEMA_IMPORT first, load this file with APPLOAD,
;;; run VELECTEMPLATESAMPLE, assign templates in the palette or with VELEC_TEMPLATE_APPLY and print
;;; the XDATA with VELECTEMPLATEXDATA after every step, _U and _REDO.

(defun velec:templateblock ()
  ;; A block definition for the INSERT objects (templates DEVICE and MOTOR accept BlockReference).
  (if (not (tblsearch "BLOCK" "VELEC_DEVICE"))
    (progn
      (entmake '((0 . "BLOCK") (2 . "VELEC_DEVICE") (70 . 0) (10 0.0 0.0 0.0)))
      (entmake '((0 . "CIRCLE") (10 0.0 0.0 0.0) (40 . 1.0)))
      (entmake '((0 . "ENDBLK"))))))

(defun velec:templateinsert (x xdata)
  (entmakex
    (append
      (list '(0 . "INSERT") '(2 . "VELEC_DEVICE") (list 10 x 0.0 0.0))
      (if xdata (list (list -3 (cons "BobrovXDATA" (cons '(1070 . 2) xdata))))))))

(defun c:VELECTEMPLATESAMPLE (/ d1 d2 d3 selection)
  (regapp "BobrovXDATA")
  (regapp "OtherApp")
  (velec:templateblock)
  ;; D1 and D2: blocks without BobrovXDATA, no DEVICE_ID and no template.
  (setq d1 (velec:templateinsert 0.0 nil))
  (setq d2 (velec:templateinsert 5.0 nil))
  ;; D3: template DEVICE with its DEVICE_ID, a value outside MOTOR (Device_Class) and another RegApp.
  (setq d3 (velec:templateinsert 10.0
    '((1000 . "DEVICE_ID") (1000 . "3F2504E0-4F89-11D3-9A0C-0305E82C3301")
      (1000 . "TEMPLATE") (1000 . "DEVICE")
      (1000 . "Device_Class") (1000 . "Щит")
      (1000 . "Power") (1040 . 2.5))))
  (entmod (append (entget d3) '((-3 ("OtherApp" (1000 . "keep") (1071 . 42))))))
  ;; L1: a line for the templates CABLE and TRAY; with the blocks no template accepts both types.
  (entmakex '((0 . "LINE") (10 0.0 -5.0 0.0) (11 10.0 -5.0 0.0)))
  (setvar "PICKFIRST" 1)
  (setq selection (ssadd d1))
  (ssadd d2 selection)
  (ssadd d3 selection)
  (sssetfirst nil selection)
  (princ "\nВыбраны блоки D1, D2 (без данных) и D3 (шаблон DEVICE, Device_Class = Щит); отрезок L1 не выбран.")
  (princ))

(defun c:VELECTEMPLATEXDATA (/ selection index item)
  (if (setq selection (ssget "_I"))
    (progn
      (setq index 0)
      (while (< index (sslength selection))
        (setq item (ssname selection index))
        (princ (strcat "\n" (cdr (assoc 0 (entget item))) " " (cdr (assoc 5 (entget item))) ": "))
        (prin1 (cdr (assoc -3 (entget item '("*")))))
        (setq index (1+ index))))
    (princ "\nВыберите объекты перед командой."))
  (princ))

(princ "\nVELECTEMPLATESAMPLE — создать блоки D1–D3 и отрезок L1; VELECTEMPLATEXDATA — вывести XDATA выбранных объектов.")
(princ)
