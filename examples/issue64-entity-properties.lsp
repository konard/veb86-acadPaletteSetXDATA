;;; Import issue64-entity-properties-schema.json into a sample DWG, load this file and run VELECENTITYSAMPLE.
;;; VELECENTITYOUTLINE then selects the 4 x 3 closed polyline.

(defun c:VELECENTITYSAMPLE (/ block-name insert-point block)
  (setq block-name "VELEC_ISSUE64" insert-point '(0.0 0.0 0.0))
  (regapp "BobrovXDATA")
  (if (not (tblsearch "BLOCK" block-name))
    (progn
      (entmake (list '(0 . "BLOCK") (cons 2 block-name) '(70 . 2) '(10 0.0 0.0 0.0)))
      (entmake '((0 . "LINE") (10 0.0 0.0 0.0) (11 4.0 0.0 0.0)))
      (entmake '((0 . "ATTDEF") (10 0.0 0.5 0.0) (40 . 0.4)
                 (1 . "A-17") (2 . "PART_CODE") (3 . "Code") (70 . 0)))
      (entmake '((0 . "ATTDEF") (10 0.0 1.1 0.0) (40 . 0.4)
                 (1 . "S-1") (2 . "PART_SERIES") (3 . "Series") (70 . 2)))
      (entmake '((0 . "ENDBLK")))))
  (entmake (list '(0 . "INSERT") (cons 2 block-name) (cons 10 insert-point) '(66 . 1)
                 '(-3 ("BobrovXDATA" (1070 . 2) (1000 . "TEMPLATE") (1000 . "PART") (1000 . "Project") (1000 . "42")))))
  (entmake '((0 . "ATTRIB") (10 0.0 0.5 0.0) (40 . 0.4) (1 . "A-17") (2 . "PART_CODE") (70 . 0)))
  (entmake '((0 . "SEQEND")))
  (setq block (entlast))
  (setq velec:issue64-outline
    (entmakex '((0 . "LWPOLYLINE") (100 . "AcDbEntity") (100 . "AcDbPolyline") (90 . 4) (70 . 1)
                 (10 0.0 5.0) (10 4.0 5.0) (10 4.0 8.0) (10 0.0 8.0)
                 (-3 ("BobrovXDATA" (1070 . 2) (1000 . "TEMPLATE") (1000 . "OUTLINE"))))))
  (setvar "PICKFIRST" 1)
  (sssetfirst nil (ssadd block))
  (princ))

(defun c:VELECENTITYOUTLINE ()
  (if (and velec:issue64-outline (entget velec:issue64-outline))
    (sssetfirst nil (ssadd velec:issue64-outline)))
  (princ))

(princ "\nVELECENTITYSAMPLE / VELECENTITYOUTLINE")
(princ)
