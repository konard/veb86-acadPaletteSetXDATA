;;; Issue #41 (comment of 2026-11-14): SYSTEMTYPE given by the TEMPLATE of the object names the group of the
;;; list above the palette, as a SYSTEMTYPE stored in XDATA does. Fixture for docs/autocad-verification.md:
;;; import examples/issue41-template-systemtype-schema.json with VELEC_SCHEMA_IMPORT, load this file with APPLOAD,
;;; run VELECSYSTEMTYPESAMPLE and look at the list: Все (6), Кабель (4), Опора (1), Отрезок (1)
;;; («Line» in the English AutoCAD). VELECSYSTEMTYPEXDATA prints the XDATA: the template objects do not store SYSTEMTYPE.

(defun velec:systemtypexdata (pairs)
  (list (list -3 (cons "BobrovXDATA" (cons '(1070 . 2) pairs)))))

(defun velec:systemtypeline (y pairs)
  (entmakex
    (append
      (list '(0 . "LINE") (list 10 0.0 y 0.0) (list 11 10.0 y 0.0))
      (if pairs (velec:systemtypexdata pairs)))))

(defun velec:systemtypeblock ()
  (if (not (tblsearch "BLOCK" "VELEC_SUPPORT"))
    (progn
      (entmake '((0 . "BLOCK") (2 . "VELEC_SUPPORT") (70 . 0) (10 0.0 0.0 0.0)))
      (entmake '((0 . "CIRCLE") (10 0.0 0.0 0.0) (40 . 1.0)))
      (entmake '((0 . "ENDBLK"))))))

(defun c:VELECSYSTEMTYPESAMPLE (/ selection)
  (regapp "BobrovXDATA")
  (velec:systemtypeblock)
  (setq selection (ssadd))
  ;; Two lines and a polyline with the template CABLE: SYSTEMTYPE «Кабель» comes from the template default.
  (ssadd (velec:systemtypeline 0.0 '((1000 . "TEMPLATE") (1000 . "CABLE"))) selection)
  (ssadd (velec:systemtypeline 2.0 '((1000 . "TEMPLATE") (1000 . "CABLE") (1000 . "Name") (1000 . "W2"))) selection)
  (ssadd
    (entmakex
      (append
        '((0 . "LWPOLYLINE") (100 . "AcDbEntity") (100 . "AcDbPolyline") (90 . 2) (70 . 0)
          (10 0.0 4.0) (10 10.0 4.0))
        (velec:systemtypexdata '((1000 . "TEMPLATE") (1000 . "CABLE")))))
    selection)
  ;; A line without a template that stores SYSTEMTYPE «Кабель»: the same group.
  (ssadd (velec:systemtypeline 6.0 '((1000 . "SYSTEMTYPE") (1000 . "Кабель"))) selection)
  ;; A block with the template SUPPORT: «Опора».
  (ssadd
    (entmakex
      (append
        (list '(0 . "INSERT") '(2 . "VELEC_SUPPORT") (list 10 15.0 0.0 0.0))
        (velec:systemtypexdata '((1000 . "TEMPLATE") (1000 . "SUPPORT")))))
    selection)
  ;; A line without data: the AutoCAD name of the type.
  (ssadd (velec:systemtypeline 8.0 nil) selection)
  (sssetfirst nil selection)
  (princ "\nВыделено 6 объектов; список: Все (6), Кабель (4), Опора (1), Отрезок (1).")
  (princ))

(defun c:VELECSYSTEMTYPEXDATA (/ selection index entity)
  (if (setq selection (ssget "_I"))
    (progn
      (setq index 0)
      (while (< index (sslength selection))
        (setq entity (ssname selection index))
        (princ (strcat "\n" (cdr (assoc 0 (entget entity))) " "))
        (princ (assoc -3 (entget entity '("BobrovXDATA"))))
        (setq index (1+ index))))
    (princ "\nВыделите объекты перед командой."))
  (princ))
