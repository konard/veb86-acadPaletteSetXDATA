;;; TZXDATA stage 4: CalculatedString fixture for docs/autocad-verification.md.
;;; Import examples/schema.json with VELEC_SCHEMA_IMPORT first (GC_Name = "@@[GC_HeadDevice].@@[GC_HDGroup]"),
;;; set XDATAPALETTE_TRACE=1, load this file with APPLOAD, run VELECCALCSAMPLE and read the trace;
;;; VELECCALCTOUCH changes the group of object B outside the palette.

(defun velec:calcline (y data)
  (entmakex
    (list '(0 . "LINE") (list 10 0.0 y 0.0) (list 11 10.0 y 0.0)
      (list -3 (cons "BobrovXDATA" (cons '(1070 . 2) data))))))

(defun c:VELECCALCSAMPLE (/ a b c selection)
  (regapp "BobrovXDATA")
  ;; A: the acceptance case of TZXDATA stage 4, ЩУ1 + 3 → ЩУ1.3.
  (setq a (velec:calcline 0.0 '((1000 . "GC_HeadDevice") (1000 . "ЩУ1") (1000 . "GC_HDGroup") (1000 . "3"))))
  ;; B: another group and an old stored GC_Name; without persist the stored text is not used.
  (setq b (velec:calcline 5.0 '((1000 . "GC_HeadDevice") (1000 . "ЩУ1") (1000 . "GC_HDGroup") (1000 . "4")
                                 (1000 . "GC_Name") (1000 . "устарело"))))
  (setq velec:calcb b)
  ;; C: the DEVICE template lists GC_Name; the missing group is substituted as empty text.
  (setq c (velec:calcline 10.0 '((1000 . "TEMPLATE") (1000 . "DEVICE") (1000 . "GC_HeadDevice") (1000 . "ЩУ2"))))
  (setvar "PICKFIRST" 1)
  (setq selection (ssadd a))
  (ssadd b selection)
  (ssadd c selection)
  (sssetfirst nil selection)
  (princ "\nТри объекта выбраны: A (ЩУ1, 3), B (ЩУ1, 4), C (шаблон DEVICE, ЩУ2 без группы).")
  (princ))

(defun c:VELECCALCTOUCH (/ data)
  (if (and velec:calcb (entget velec:calcb))
    (progn
      (setq data (entget velec:calcb '("BobrovXDATA")))
      (entmod (subst '(-3 ("BobrovXDATA" (1070 . 2)
                            (1000 . "GC_HeadDevice") (1000 . "ЩУ1") (1000 . "GC_HDGroup") (1000 . "3")
                            (1000 . "GC_Name") (1000 . "устарело")))
                     (assoc -3 data) data))
      (princ (strcat "\nУ объекта B " (cdr (assoc 5 (entget velec:calcb))) " группа изменена на 3.")))
    (princ "\nСначала выполните VELECCALCSAMPLE."))
  (princ))

(princ "\nVELECCALCSAMPLE — создать объекты с GC_Name; VELECCALCTOUCH — изменить группу объекта B.")
(princ)
